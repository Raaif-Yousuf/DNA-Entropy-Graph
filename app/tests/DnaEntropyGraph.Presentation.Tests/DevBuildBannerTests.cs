using CommunityToolkit.Mvvm.Messaging;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Presentation.Services;
using DnaEntropyGraph.Presentation.ViewModels;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

/// <summary>Issue #263: an unsigned or development build says so in the title bar, and About names the exact build.</summary>
public sealed class DevBuildBannerTests
{
    [Theory]
    [InlineData("0.0.1+abc1234def5678", "0.0.1", "abc1234")]
    [InlineData("1.2.3-beta.1+0123456789abcdef", "1.2.3-beta.1", "0123456")]
    [InlineData("0.0.1", "0.0.1", "")]
    [InlineData("", "0.0.0", "")]
    [InlineData(null, "0.0.0", "")]
    [InlineData("1.0.0+", "1.0.0", "")]
    public void The_informational_version_is_split_into_a_version_and_a_short_sha(string? informational, string version, string sha)
    {
        var info = BuildInfo.Parse(informational);

        info.Version.ShouldBe(version);
        info.ShortSha.ShouldBe(sha);
    }

    [Fact]
    public void The_display_text_names_the_sha_only_when_there_is_one()
    {
        BuildInfo.Parse("1.0.0+abc1234def").Display.ShouldBe("1.0.0 (abc1234)");
        BuildInfo.Parse("1.0.0").Display.ShouldBe("1.0.0");
    }

    [Fact]
    public void An_unsigned_build_is_a_dev_build_and_a_signed_one_is_not()
    {
        new BuildIdentity(BuildInfo.Parse("1.0.0+abc1234"), isSigned: false).IsDevBuild.ShouldBeTrue();
        new BuildIdentity(BuildInfo.Parse("1.0.0+abc1234"), isSigned: true).IsDevBuild.ShouldBeFalse();
    }

    private static IStringResourceProvider Strings()
    {
        var strings = Substitute.For<IStringResourceProvider>();
        strings.GetString("DevBuild_Banner").Returns("Development build {0}");
        strings.GetString(Arg.Is<string>(k => k != "DevBuild_Banner")).Returns(call => call.Arg<string>());
        return strings;
    }

    private static ShellViewModel Shell(BuildIdentity? build)
        => new(Substitute.For<INavigator>(), Substitute.For<IGcpAccount>(), new WeakReferenceMessenger(), Strings(), TestMessages.Center(), TestMessages.Completion(), build: build);

    [Fact]
    public void An_unsigned_build_shows_the_banner_with_the_version_and_sha()
    {
        var shell = Shell(new BuildIdentity(BuildInfo.Parse("0.0.1+abc1234def"), isSigned: false));

        shell.HasDevBuildBanner.ShouldBeTrue();
        shell.DevBuildBannerText.ShouldBe("Development build 0.0.1 (abc1234)");
    }

    [Fact]
    public void A_signed_build_shows_no_banner()
    {
        var shell = Shell(new BuildIdentity(BuildInfo.Parse("0.0.1+abc1234def"), isSigned: true));

        shell.HasDevBuildBanner.ShouldBeFalse();
        shell.DevBuildBannerText.ShouldBeEmpty();
    }

    [Fact]
    public void No_build_identity_means_no_banner()
    {
        Shell(null).HasDevBuildBanner.ShouldBeFalse();
    }

    [Fact]
    public void About_names_the_exact_build()
    {
        var strings = Strings();
        strings.GetString("AboutBuild_Text").Returns("Build {0}");
        var vm = new UpdateViewModel(Substitute.For<IAppUpdateSource>(), new UpdateSettings(Substitute.For<ISettingsStore>()), strings, Substitute.For<IActiveRunProbe>(), build: new BuildIdentity(BuildInfo.Parse("0.0.1+abc1234def"), isSigned: false));

        vm.BuildText.ShouldBe("Build 0.0.1 (abc1234)");
    }
}
