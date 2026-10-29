using System.Net;
using DnaEntropyGraph.Cloud.Auth;
using DnaEntropyGraph.Cloud.Tests.Auth;
using DnaEntropyGraph.Core.Net;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests.Net;

/// <summary>Issue #239 / Hard Rule 12: the manual proxy sign-in is an encrypted file, never plain text.</summary>
public class ProxyCredentialFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "deg-proxycred-" + Guid.NewGuid().ToString("n"));

    private ProxyCredentialFile NewStore() => new(Path.Combine(_dir, "nested", "proxy.cred"), new XorProtector());

    [Fact]
    public void A_saved_sign_in_comes_back_and_the_file_holds_neither_the_name_nor_the_password_in_the_clear()
    {
        var store = NewStore();

        store.Save(new NetworkCredential("alice", "hunter2 \u00e9"));

        var loaded = NewStore().Load();
        loaded!.UserName.ShouldBe("alice");
        loaded.Password.ShouldBe("hunter2 \u00e9");
        var raw = File.ReadAllText(Path.Combine(_dir, "nested", "proxy.cred"), System.Text.Encoding.Latin1);
        raw.ShouldNotContain("alice");
        raw.ShouldNotContain("hunter2");
    }

    [Fact]
    public void No_file_is_no_sign_in_and_clearing_twice_is_fine()
    {
        var store = NewStore();

        store.Load().ShouldBeNull();
        store.Clear();
        store.Save(new NetworkCredential("a", "b"));
        store.Clear();
        store.Clear();
        store.Load().ShouldBeNull();
    }

    [Fact]
    public void A_file_this_user_cannot_decrypt_is_a_named_failure_not_a_crash()
    {
        var store = NewStore();
        store.Save(new NetworkCredential("a", "b"));
        File.WriteAllBytes(Path.Combine(_dir, "nested", "proxy.cred"), [1, 2, 3]);

        Should.Throw<ProxyCredentialUnreadableException>(() => store.Load());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
