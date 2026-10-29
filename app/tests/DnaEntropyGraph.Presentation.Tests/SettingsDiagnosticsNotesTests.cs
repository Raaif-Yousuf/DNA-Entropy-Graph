using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Diagnostics;
using DnaEntropyGraph.Presentation.Services;
using DnaEntropyGraph.Presentation.ViewModels;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

/// <summary>Issue #141: the "include my notes and tags" box beside Save diagnostics. Off unless the user ticks it, every time.</summary>
public sealed class SettingsDiagnosticsNotesTests
{
    private readonly IDiagnosticsExporter _exporter = Substitute.For<IDiagnosticsExporter>();
    private readonly IFilePicker _picker = Substitute.For<IFilePicker>();
    private readonly SettingsViewModel _viewModel;

    public SettingsDiagnosticsNotesTests()
    {
        var strings = Substitute.For<IStringResourceProvider>();
        strings.GetString(Arg.Any<string>()).Returns(call => call.Arg<string>() + "|{0}");
        _picker.PickSaveZipAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("C:\\out\\d.zip");
        _viewModel = new SettingsViewModel(
            Substitute.For<ISettingsStore>(), Substitute.For<IToastService>(), strings, _exporter, _picker, Substitute.For<IFolderLauncher>(),
            TimeProvider.System, Substitute.For<IThemeApplier>());
    }

    [Fact]
    public async Task The_box_is_off_by_default_and_the_export_is_asked_to_leave_notes_and_tags_out()
    {
        _viewModel.IncludeNotesAndTags.ShouldBeFalse();

        await _viewModel.SaveDiagnosticsCommand.ExecuteAsync(null);

        await _exporter.Received(1).ExportAsync("C:\\out\\d.zip", Arg.Any<CancellationToken>(), false);
    }

    [Fact]
    public async Task Ticking_the_box_asks_the_export_to_include_them()
    {
        _viewModel.IncludeNotesAndTags = true;

        await _viewModel.SaveDiagnosticsCommand.ExecuteAsync(null);

        await _exporter.Received(1).ExportAsync("C:\\out\\d.zip", Arg.Any<CancellationToken>(), true);
    }

    [Fact]
    public async Task The_box_unticks_itself_after_a_save_so_the_next_bundle_is_private_again()
    {
        _viewModel.IncludeNotesAndTags = true;
        await _viewModel.SaveDiagnosticsCommand.ExecuteAsync(null);

        _viewModel.IncludeNotesAndTags.ShouldBeFalse();
    }

    [Fact]
    public async Task The_box_stays_ticked_while_the_file_is_still_being_written()
    {
        var started = new TaskCompletionSource();
        var gate = new TaskCompletionSource();
        _exporter.ExportAsync(Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<bool>()).Returns(_ =>
        {
            started.TrySetResult();
            return gate.Task;
        });
        _viewModel.IncludeNotesAndTags = true;

        var saving = _viewModel.SaveDiagnosticsCommand.ExecuteAsync(null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        _viewModel.IncludeNotesAndTags.ShouldBeTrue();
        gate.SetResult();
        await saving;
        _viewModel.IncludeNotesAndTags.ShouldBeFalse();
    }

    [Fact]
    public async Task A_choice_made_while_the_file_is_being_written_is_kept_for_the_next_file()
    {
        var started = new TaskCompletionSource();
        var gate = new TaskCompletionSource();
        _exporter.ExportAsync(Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<bool>()).Returns(_ =>
        {
            started.TrySetResult();
            return gate.Task;
        });

        var saving = _viewModel.SaveDiagnosticsCommand.ExecuteAsync(null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        _viewModel.IncludeNotesAndTags = true;
        gate.SetResult();
        await saving;

        await _exporter.Received(1).ExportAsync("C:\\out\\d.zip", Arg.Any<CancellationToken>(), false);
        _viewModel.IncludeNotesAndTags.ShouldBeTrue();
    }

    [Fact]
    public async Task Unticking_and_re_ticking_the_box_during_the_write_keeps_it_ticked_for_the_next_file()
    {
        // Cold review round 4: true -> false -> true during the write ends equal to the captured value, so a value comparison cleared the re-tick.
        var started = new TaskCompletionSource();
        var gate = new TaskCompletionSource();
        _exporter.ExportAsync(Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<bool>()).Returns(_ =>
        {
            started.TrySetResult();
            return gate.Task;
        });
        _viewModel.IncludeNotesAndTags = true;

        var saving = _viewModel.SaveDiagnosticsCommand.ExecuteAsync(null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        _viewModel.IncludeNotesAndTags = false;
        _viewModel.IncludeNotesAndTags = true;
        gate.SetResult();
        await saving;

        await _exporter.Received(1).ExportAsync("C:\\out\\d.zip", Arg.Any<CancellationToken>(), true);
        _viewModel.IncludeNotesAndTags.ShouldBeTrue();
    }

    [Fact]
    public async Task A_cancelled_picker_keeps_the_box_ticked()
    {
        _picker.PickSaveZipAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((string?)null);
        _viewModel.IncludeNotesAndTags = true;

        await _viewModel.SaveDiagnosticsCommand.ExecuteAsync(null);

        _viewModel.IncludeNotesAndTags.ShouldBeTrue();
        await _exporter.DidNotReceive().ExportAsync(Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task A_cancelled_write_keeps_the_box_ticked()
    {
        _exporter.ExportAsync(Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<bool>()).Returns(Task.FromCanceled(new CancellationToken(true)));
        _viewModel.IncludeNotesAndTags = true;

        await _viewModel.SaveDiagnosticsCommand.ExecuteAsync(null);

        _viewModel.IncludeNotesAndTags.ShouldBeTrue();
    }

    [Fact]
    public async Task A_write_failure_keeps_the_box_ticked()
    {
        _exporter.ExportAsync(Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<bool>()).Returns(Task.FromException(new IOException("disk full")));
        _viewModel.IncludeNotesAndTags = true;

        await _viewModel.SaveDiagnosticsCommand.ExecuteAsync(null);

        _viewModel.IncludeNotesAndTags.ShouldBeTrue();
    }

    [Fact]
    public async Task A_refused_bundle_keeps_the_box_ticked()
    {
        _exporter.ExportAsync(Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<bool>()).Returns(Task.FromException(new DiagnosticsLeakException(new LeakFinding("x", LeakRules.Identifier))));
        _viewModel.IncludeNotesAndTags = true;

        await _viewModel.SaveDiagnosticsCommand.ExecuteAsync(null);

        _viewModel.IncludeNotesAndTags.ShouldBeTrue();
    }
}
