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
    public void The_running_build_identity_reads_a_version()
    {
        var identity = BuildIdentityFactory.Create();

        identity.Info.Version.ShouldNotBeNullOrWhiteSpace();
    }
}
