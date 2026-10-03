using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Viewers;
using DnaEntropyGraph.Presentation.Services;
using DnaEntropyGraph.Presentation.ViewModels;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

/// <summary>Issue #641: the Settings External viewers group, read and written through <see cref="ViewerSettingKeys"/>.</summary>
public sealed class ExternalViewersSettingsTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "deg-viewer-settings", Guid.NewGuid().ToString("N"));
    private readonly IFilePicker _picker = Substitute.For<IFilePicker>();
    private readonly IStringResourceProvider _strings = Substitute.For<IStringResourceProvider>();
    private readonly MemorySettings _settings = new();

    public ExternalViewersSettingsTests()
    {
        Directory.CreateDirectory(_base);
        _strings.GetString(Arg.Any<string>()).Returns(call => call.Arg<string>());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_base, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private ExternalViewersSettingsViewModel Create() => new(_settings, _picker, _strings);

    private static IReadOnlyList<RunOutputFile> IgvFiles() =>
    [
        new RunOutputFile("a/a.fasta", @"C:\a\a.fasta", 1),
        new RunOutputFile("a/a.entropy.bedgraph", @"C:\a\a.entropy.bedgraph", 1),
    ];

    private string RealProgram()
    {
        var path = Path.Combine(_base, "igv.bat");
        File.WriteAllText(path, "rem");
        return path;
    }

    [Fact]
    public void A_fresh_install_shows_no_paths_and_the_default_port()
    {
        var vm = Create();

        vm.IgvPath.ShouldBeEmpty();
        vm.GeneiousPath.ShouldBeEmpty();
        vm.IgvPortText.ShouldBe("60151");
        vm.IgvPathStatus.ShouldBeEmpty();
        vm.IgvPortStatus.ShouldBeEmpty();
    }

    [Fact]
    public void Saved_values_are_shown_when_the_page_opens()
    {
        _settings.Values[ViewerSettingKeys.IgvPath] = @"C:\igv\igv.bat";
        _settings.Values[ViewerSettingKeys.GeneiousPath] = @"C:\g\Geneious.exe";
        _settings.Values[ViewerSettingKeys.IgvPort] = "60999";

        var vm = Create();

        vm.IgvPath.ShouldBe(@"C:\igv\igv.bat");
        vm.GeneiousPath.ShouldBe(@"C:\g\Geneious.exe");
        vm.IgvPortText.ShouldBe("60999");
    }

    [Fact]
    public void A_saved_port_that_is_not_a_valid_number_is_shown_as_the_default()
    {
        _settings.Values[ViewerSettingKeys.IgvPort] = "banana";

        Create().IgvPortText.ShouldBe("60151");
    }

    [Fact]
    public void A_valid_port_is_saved_under_the_viewer_key()
    {
        var vm = Create();

        vm.IgvPortText = "60152";

        _settings.Values[ViewerSettingKeys.IgvPort].ShouldBe("60152");
        vm.IgvPortStatus.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("60 151")]
    public void An_invalid_port_is_refused_with_a_message_and_not_saved(string text)
    {
        _settings.Values[ViewerSettingKeys.IgvPort] = "60999";
        var vm = Create();

        vm.IgvPortText = text;

        vm.IgvPortStatus.ShouldBe("ViewerPortInvalid");
        _settings.Values[ViewerSettingKeys.IgvPort].ShouldBe("60999");
    }

    [Theory]
    [InlineData("1")]
    [InlineData("65535")]
    public void The_port_range_ends_are_accepted(string text)
    {
        var vm = Create();

        vm.IgvPortText = text;

        _settings.Values[ViewerSettingKeys.IgvPort].ShouldBe(text);
        vm.IgvPortStatus.ShouldBeEmpty();
    }

    [Fact]
    public void A_path_that_exists_is_saved_with_no_warning()
    {
        var program = RealProgram();
        var vm = Create();

        vm.IgvPath = program;

        _settings.Values[ViewerSettingKeys.IgvPath].ShouldBe(program);
        vm.IgvPathStatus.ShouldBeEmpty();
    }

    [Fact]
    public void A_path_that_does_not_exist_is_flagged_not_silently_accepted()
    {
        var vm = Create();

        vm.GeneiousPath = Path.Combine(_base, "missing.exe");

        vm.GeneiousPathStatus.ShouldBe("ViewerPathMissing");
        vm.IgvPathStatus.ShouldBeEmpty();
    }

    [Fact]
    public void A_pasted_path_in_quotes_is_trimmed_before_it_is_checked_and_saved()
    {
        var program = RealProgram();
        var vm = Create();

        vm.IgvPath = "  \"" + program + "\" ";

        _settings.Values[ViewerSettingKeys.IgvPath].ShouldBe(program);
        vm.IgvPathStatus.ShouldBeEmpty();
    }

    [Fact]
    public void Clearing_a_path_saves_empty_and_clears_the_warning()
    {
        var vm = Create();
        vm.IgvPath = Path.Combine(_base, "missing.exe");

        vm.IgvPath = string.Empty;

        _settings.Values[ViewerSettingKeys.IgvPath].ShouldBeEmpty();
        vm.IgvPathStatus.ShouldBeEmpty();
    }

    [Fact]
    public async Task Browse_fills_the_row_with_the_chosen_program_and_saves_it()
    {
        var program = RealProgram();
        _picker.PickProgramAsync(Arg.Any<CancellationToken>()).Returns(program);
        var vm = Create();

        await vm.BrowseGeneiousCommand.ExecuteAsync(null);

        vm.GeneiousPath.ShouldBe(program);
        _settings.Values[ViewerSettingKeys.GeneiousPath].ShouldBe(program);
        _settings.Values.ContainsKey(ViewerSettingKeys.IgvPath).ShouldBeFalse();
    }

    [Fact]
    public async Task Cancelling_Browse_changes_nothing()
    {
        _settings.Values[ViewerSettingKeys.IgvPath] = @"C:\igv\igv.bat";
        _picker.PickProgramAsync(Arg.Any<CancellationToken>()).Returns((string?)null);
        var vm = Create();

        await vm.BrowseIgvCommand.ExecuteAsync(null);

        vm.IgvPath.ShouldBe(@"C:\igv\igv.bat");
    }

    [Fact]
    public void Reset_to_default_clears_each_row_on_its_own()
    {
        _settings.Values[ViewerSettingKeys.IgvPath] = @"C:\igv\igv.bat";
        _settings.Values[ViewerSettingKeys.GeneiousPath] = @"C:\g\Geneious.exe";
        _settings.Values[ViewerSettingKeys.IgvPort] = "60999";
        var vm = Create();
        vm.IgvPortText = "0";

        vm.ResetIgvPortCommand.Execute(null);
        vm.IgvPortText.ShouldBe("60151");
        vm.IgvPortStatus.ShouldBeEmpty();
        _settings.Values[ViewerSettingKeys.IgvPort].ShouldBe("60151");
        vm.IgvPath.ShouldBe(@"C:\igv\igv.bat");

        vm.ResetIgvPathCommand.Execute(null);
        vm.IgvPath.ShouldBeEmpty();
        _settings.Values[ViewerSettingKeys.IgvPath].ShouldBeEmpty();
        vm.GeneiousPath.ShouldBe(@"C:\g\Geneious.exe");

        vm.ResetGeneiousPathCommand.Execute(null);
        vm.GeneiousPath.ShouldBeEmpty();
        _settings.Values[ViewerSettingKeys.GeneiousPath].ShouldBeEmpty();
    }

    [Fact]
    public void A_locked_settings_file_neither_crashes_the_page_nor_pretends_to_save()
    {
        _settings.Unavailable = true;
        var vm = Create();

        vm.IgvPortText = "60152";

        vm.IgvPortStatus.ShouldBe("ViewerSettingNotSaved");
    }

    [Fact]
    public async Task A_port_set_in_Settings_is_the_port_Open_in_IGV_connects_to()
    {
        var vm = Create();
        vm.IgvPortText = "60152";
        var client = Substitute.For<IIgvBatchClient>();
        client.SendAsync(Arg.Any<int>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns(IgvBatchOutcome.Done);
        var opener = new ExternalViewerOpener(client, Substitute.For<IViewerProcessLauncher>(), Substitute.For<IViewerLocator>(), _settings, _picker);

        await opener.OpenInIgvAsync(
            IgvFiles(),
            TestContext.Current.CancellationToken);

        await client.Received(1).SendAsync(60152, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());

        // And the next press after a change in Settings uses the new value, not a copy taken earlier.
        vm.IgvPortText = "60153";
        await opener.OpenInIgvAsync(
            IgvFiles(),
            TestContext.Current.CancellationToken);
        await client.Received(1).SendAsync(60153, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_program_path_set_in_Settings_is_the_program_Open_in_Geneious_starts()
    {
        var program = RealProgram();
        var vm = Create();
        vm.GeneiousPath = program;
        var launcher = Substitute.For<IViewerProcessLauncher>();
        launcher.Launch(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>()).Returns(true);
        var opener = new ExternalViewerOpener(Substitute.For<IIgvBatchClient>(), launcher, Substitute.For<IViewerLocator>(), _settings, _picker);

        await opener.OpenInGeneiousAsync([new RunOutputFile("a/a.gb", @"C:\a\a.gb", 1)], TestContext.Current.CancellationToken);

        launcher.Received(1).Launch(program, Arg.Any<IReadOnlyList<string>>());
    }

    private sealed class MemorySettings : ISettingsStore
    {
        public Dictionary<string, string> Values { get; } = [];

        public bool Unavailable { get; set; }

        public string? GetString(string key)
            => Unavailable ? throw new SettingsUnavailableException("locked") : Values.GetValueOrDefault(key);

        public void SetString(string key, string value)
        {
            if (Unavailable)
            {
                throw new SettingsUnavailableException("locked");
            }

            Values[key] = value;
        }
    }
}
