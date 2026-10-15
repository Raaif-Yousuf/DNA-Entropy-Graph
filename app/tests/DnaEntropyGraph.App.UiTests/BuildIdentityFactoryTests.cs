using DnaEntropyGraph.App.Services;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.App.UiTests;

/// <summary>Issue #263: the signed/unsigned decision behind the title-bar banner, on real files (no window needed).</summary>
public class BuildIdentityFactoryTests
{
    [Fact]
    public void An_unsigned_file_is_not_signed()
    {
        // This test assembly is built locally and never signed.
        BuildIdentityFactory.HasAuthenticodeSignature(typeof(BuildIdentityFactoryTests).Assembly.Location).ShouldBeFalse();
    }

    [Fact]
    public void A_file_with_an_embedded_signature_is_signed()
    {
        // The .NET runtime's own core library carries Microsoft's embedded Authenticode signature.
        BuildIdentityFactory.HasAuthenticodeSignature(typeof(object).Assembly.Location).ShouldBeTrue();
    }

    [Fact]
    public void A_missing_or_empty_path_is_not_signed()
    {
        BuildIdentityFactory.HasAuthenticodeSignature(null).ShouldBeFalse();
        BuildIdentityFactory.HasAuthenticodeSignature(string.Empty).ShouldBeFalse();
        BuildIdentityFactory.HasAuthenticodeSignature(Path.Combine(Path.GetTempPath(), "deg-no-such-file.exe")).ShouldBeFalse();
    }

    [Fact]
    public void A_launch_through_dotnet_exe_checks_the_apps_own_binary_not_the_hosts_signature()
    {
        // `dotnet DnaEntropyGraph.App.dll`: ProcessPath is dotnet.exe, which Microsoft signs. The app's own files are what count.
        var dotnetHost = typeof(object).Assembly.Location; // a Microsoft-signed file standing in for dotnet.exe
        BuildIdentityFactory.HasAuthenticodeSignature(dotnetHost).ShouldBeTrue("the stand-in host must be signed or this test proves nothing");

        var own = BuildIdentityFactory.ResolveOwnBinary(typeof(BuildIdentityFactoryTests).Assembly.Location, dotnetHost);

        own.ShouldNotBe(dotnetHost);
        BuildIdentityFactory.HasAuthenticodeSignature(own).ShouldBeFalse();
    }

    [Fact]
    public void The_own_binary_is_the_apphost_next_to_the_dll_when_there_is_one_and_the_dll_otherwise()
    {
        var dir = Path.Combine(Path.GetTempPath(), "deg-own-bin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var dll = Path.Combine(dir, "App.dll");
            File.WriteAllText(dll, "x");
            BuildIdentityFactory.ResolveOwnBinary(dll, "C:\\dotnet\\dotnet.exe").ShouldBe(dll);

            var exe = Path.Combine(dir, "App.exe");
            File.WriteAllText(exe, "x");
            BuildIdentityFactory.ResolveOwnBinary(dll, "C:\\dotnet\\dotnet.exe").ShouldBe(exe);

            // A single-file publish has no assembly location: the process is the app.
            BuildIdentityFactory.ResolveOwnBinary(string.Empty, exe).ShouldBe(exe);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void A_file_the_check_cannot_read_never_throws_out_of_startup()
    {
        var dir = Path.Combine(Path.GetTempPath(), "deg-sig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var junk = Path.Combine(dir, "junk.exe");
            File.WriteAllBytes(junk, [1, 2, 3]);
            BuildIdentityFactory.HasAuthenticodeSignature(junk).ShouldBeFalse();

            // A file held open exclusively raises IOException, which the old catch (CryptographicException only) let escape.
            using var held = new FileStream(junk, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            BuildIdentityFactory.HasAuthenticodeSignature(junk).ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void The_running_build_identity_reads_a_version()
    {
        var identity = BuildIdentityFactory.Create();

        identity.Info.Version.ShouldNotBeNullOrWhiteSpace();
    }
}
