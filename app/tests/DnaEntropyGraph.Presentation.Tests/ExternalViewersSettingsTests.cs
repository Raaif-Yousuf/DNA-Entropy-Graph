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

    private ExternalViewersSettingsViewModel Create(IFileProbe? probe = null) => new(_settings, _picker, _strings, new InlineDispatcher(), probe ?? new FuncProbe(File.Exists));

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

    [Fact]
    public void A_pasted_path_is_written_back_to_the_box_trimmed_and_unquoted()
    {
        var program = RealProgram();
        var vm = Create();

        vm.IgvPath = "  \"" + program + "\" ";

        vm.IgvPath.ShouldBe(program);
        vm.IgvPathStatus.ShouldBeEmpty();
        _settings.Values[ViewerSettingKeys.IgvPath].ShouldBe(program);
    }

    [Fact]
    public void A_port_with_leading_zeros_is_shown_as_the_number_that_was_saved()
    {
        var vm = Create();

        vm.IgvPortText = "060151";

        vm.IgvPortText.ShouldBe("60151");
        _settings.Values[ViewerSettingKeys.IgvPort].ShouldBe("60151");
        vm.IgvPortStatus.ShouldBeEmpty();
    }

    [Fact]
    public void Opening_the_page_does_not_wait_for_a_path_on_a_slow_share()
    {
        _settings.Values[ViewerSettingKeys.IgvPath] = @"\\nas\share\igv.bat";
        var probe = new GatedProbe();

        var vm = Create(probe);

        // The constructor returned while the probe is still pending: nothing waited for the share.
        probe.Pending.Count.ShouldBe(1);
        vm.IgvPathStatus.ShouldBeEmpty();

        probe.Pending[0].SetResult(false);

        vm.IgvPathStatus.ShouldBe("ViewerPathMissing");
    }

    [Fact]
    public void Typing_a_path_does_not_wait_for_the_check_and_the_status_follows_it()
    {
        var probe = new GatedProbe();
        var vm = Create(probe);

        vm.GeneiousPath = @"\\nas\share\Geneious.exe";

        probe.Pending.Count.ShouldBe(1);
        vm.GeneiousPathStatus.ShouldBeEmpty();
        _settings.Values[ViewerSettingKeys.GeneiousPath].ShouldBe(@"\\nas\share\Geneious.exe");

        probe.Pending[0].SetResult(false);
        vm.GeneiousPathStatus.ShouldBe("ViewerPathMissing");
    }

    [Fact]
    public void A_late_answer_for_an_earlier_path_does_not_overwrite_the_status_of_the_current_one()
    {
        var probe = new GatedProbe();
        var vm = Create(probe);

        vm.IgvPath = @"\\nas\a\igv.bat";
        vm.IgvPath = @"\\nas\b\igv.bat";
        probe.Pending[1].SetResult(true);
        probe.Pending[0].SetResult(false);

        vm.IgvPathStatus.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_saved_path_blanked_after_a_failed_launch_is_gone_the_next_time_Settings_opens()
    {
        // Settings pages are not cached (no NavigationCacheMode anywhere in the app), so every visit builds a new view
        // model that reads the store; Open in IGV is on another page, so the Settings page cannot be open during a launch.
        var program = RealProgram();
        _settings.Values[ViewerSettingKeys.IgvPath] = program;
        var launcher = Substitute.For<IViewerProcessLauncher>();
        launcher.Launch(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>()).Returns(false);
        var client = Substitute.For<IIgvBatchClient>();
        client.SendAsync(Arg.Any<int>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns(IgvBatchOutcome.NotListening);
        var opener = new ExternalViewerOpener(client, launcher, Substitute.For<IViewerLocator>(), _settings, _picker);
        Create().IgvPath.ShouldBe(program);

        await opener.OpenInIgvAsync(IgvFiles(), TestContext.Current.CancellationToken);

        Create().IgvPath.ShouldBeEmpty();
    }

    [Fact]
    public void Reset_port_writes_the_default_when_the_box_already_shows_it_but_the_store_holds_another()
    {
        var vm = Create();
        vm.IgvPortText.ShouldBe("60151");
        _settings.Values[ViewerSettingKeys.IgvPort] = "60999";

        vm.ResetIgvPortCommand.Execute(null);

        _settings.Values[ViewerSettingKeys.IgvPort].ShouldBe("60151");
        vm.IgvPortStatus.ShouldBeEmpty();
    }

    [Fact]
    public void A_locked_settings_file_is_reported_for_a_path_write()
    {
        var vm = Create();
        _settings.Unavailable = true;

        vm.IgvPath = @"C:\igv\igv.bat";

        vm.IgvPathStatus.ShouldBe("ViewerSettingNotSaved");
    }

    [Fact]
    public void A_locked_settings_file_is_reported_for_a_reset_port_write_in_both_branches()
    {
        var vm = Create();
        _settings.Unavailable = true;

        vm.ResetIgvPortCommand.Execute(null); // box already shows the default
        vm.IgvPortStatus.ShouldBe("ViewerSettingNotSaved");

        _settings.Unavailable = false;
        vm.IgvPortText = "60152";
        _settings.Unavailable = true;
        vm.ResetIgvPortCommand.Execute(null); // box differs from the default
        vm.IgvPortText.ShouldBe("60151");
        vm.IgvPortStatus.ShouldBe("ViewerSettingNotSaved");
    }

    private sealed class InlineDispatcher : IDispatcher
    {
        public void Enqueue(Action action) => action();
    }

    private sealed class FuncProbe(Func<string, bool> exists) : IFileProbe
    {
        public Task<bool> ExistsAsync(string path) => Task.FromResult(exists(path));
    }

    private sealed class GatedProbe : IFileProbe
    {
        public List<TaskCompletionSource<bool>> Pending { get; } = [];

        public Task<bool> ExistsAsync(string path)
        {
            var gate = new TaskCompletionSource<bool>();
            Pending.Add(gate);
            return gate.Task;
        }
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
