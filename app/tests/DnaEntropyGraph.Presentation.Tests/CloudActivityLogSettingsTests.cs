using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Presentation.Services;
using DnaEntropyGraph.Presentation.ViewModels;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

/// <summary>Issue #245: Settings > Diagnostics "Open cloud activity log" reveals the audit file under the data root, or says there is nothing yet.</summary>
public sealed class CloudActivityLogSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "deg-audit-settings", Guid.NewGuid().ToString("N"));
    private readonly IFolderLauncher _launcher = Substitute.For<IFolderLauncher>();
    private readonly IStringResourceProvider _strings = Substitute.For<IStringResourceProvider>();

    public CloudActivityLogSettingsTests()
    {
        Directory.CreateDirectory(_root);
        _strings.GetString(Arg.Any<string>()).Returns(call => call.Arg<string>());
        _launcher.RevealFile(Arg.Any<string>()).Returns(true);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private AppDataRoot Root => AppDataRoot.FromPath(_root);

    [Fact]
    public void With_a_log_the_button_reveals_that_file_under_the_data_root()
    {
        Directory.CreateDirectory(Root.LogsDirectory);
        File.WriteAllText(Root.CloudAuditLogFile, "{}\n");
        var vm = new CloudActivityLogSettingsViewModel(Root, _launcher, _strings);

        vm.OpenCloudActivityLogCommand.Execute(null);

        _launcher.Received(1).RevealFile(Root.CloudAuditLogFile);
        vm.CloudActivityStatus.ShouldBeEmpty();
    }

    [Fact]
    public void With_no_log_yet_it_says_so_and_opens_nothing()
    {
        var vm = new CloudActivityLogSettingsViewModel(Root, _launcher, _strings);

        vm.OpenCloudActivityLogCommand.Execute(null);

        _launcher.DidNotReceive().RevealFile(Arg.Any<string>());
        vm.CloudActivityStatus.ShouldBe("CloudActivityLogNone");
    }

    [Fact]
    public void A_launcher_that_cannot_open_it_or_throws_is_reported_never_thrown()
    {
        Directory.CreateDirectory(Root.LogsDirectory);
        File.WriteAllText(Root.CloudAuditLogFile, "{}\n");
        _launcher.RevealFile(Arg.Any<string>()).Returns(_ => throw new InvalidOperationException("x"));
        var vm = new CloudActivityLogSettingsViewModel(Root, _launcher, _strings);

        Should.NotThrow(() => vm.OpenCloudActivityLogCommand.Execute(null));

        vm.CloudActivityStatus.ShouldBe("CloudActivityLogOpenFailed");
    }

    [Fact]
    public void The_log_lives_under_the_override_root_so_portable_and_profile_modes_follow_it()
    {
        Root.CloudAuditLogFile.ShouldStartWith(Path.GetFullPath(_root));
        Path.GetFileName(Root.CloudAuditLogFile).ShouldBe("cloud-audit.jsonl");
    }
}
