using System.Text;
using DnaEntropyGraph.Cloud.Auth;
using Google.Apis.Auth.OAuth2.Responses;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests.Auth;

public class DpapiTokenStoreTests
{
    private static string TempDir() => Path.Combine(Path.GetTempPath(), "deg-store-" + Guid.NewGuid().ToString("n"));

    [Fact]
    public async Task Round_trips_a_token_and_the_file_does_not_hold_it_in_the_clear_under_the_real_DPAPI()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DPAPI is Windows only");
        var dir = TempDir();
        var store = new DpapiTokenStore(dir, new DpapiSecretProtector());

        await store.StoreAsync("1001", new TokenResponse { RefreshToken = "refresh-secret-xyz", AccessToken = "access-secret-abc" });

        var raw = Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(dir, "1001.tok")));
        raw.ShouldNotContain("refresh-secret-xyz");
        raw.ShouldNotContain("access-secret-abc");
        var back = await new DpapiTokenStore(dir, new DpapiSecretProtector()).GetAsync<TokenResponse>("1001");
        back.RefreshToken.ShouldBe("refresh-secret-xyz");
        back.AccessToken.ShouldBe("access-secret-abc");
    }

    [Fact]
    public async Task Missing_deleted_and_corrupt_files_read_as_no_token()
    {
        var dir = TempDir();
        var store = new DpapiTokenStore(dir, new XorProtector());

        (await store.GetAsync<TokenResponse>("1001")).ShouldBeNull();
        await store.StoreAsync("1001", new TokenResponse { RefreshToken = "r" });
        await store.DeleteAsync<TokenResponse>("1001");
        File.Exists(Path.Combine(dir, "1001.tok")).ShouldBeFalse();
        await store.DeleteAsync<TokenResponse>("1001"); // deleting twice is fine
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "2002.tok"), [9, 9, 9]);
        (await new DpapiTokenStore(dir, new ThrowingProtector()).GetAsync<TokenResponse>("2002")).ShouldBeNull();
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("a/b")]
    [InlineData("")]
    [InlineData("a b")]
    public async Task A_key_that_is_not_a_safe_file_name_is_refused(string key)
    {
        var store = new DpapiTokenStore(TempDir(), new XorProtector());

        await Should.ThrowAsync<ArgumentException>(() => store.StoreAsync(key, new TokenResponse()));
    }

    private sealed class ThrowingProtector : ISecretProtector
    {
        public byte[] Protect(byte[] plain) => plain;

        public byte[] Unprotect(byte[] protectedBytes) => throw new System.Security.Cryptography.CryptographicException("not ours");
    }
}
