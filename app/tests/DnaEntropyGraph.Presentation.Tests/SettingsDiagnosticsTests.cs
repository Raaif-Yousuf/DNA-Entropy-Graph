using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Diagnostics;
using DnaEntropyGraph.Presentation.Services;
using DnaEntropyGraph.Presentation.ViewModels;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

/// <summary>Issue #106: the Save diagnostics command the run-error strings send the user to.</summary>
public class SettingsDiagnosticsTests
{
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private readonly IDiagnosticsExporter _exporter = Substitute.For<IDiagnosticsExporter>();
    private readonly IFilePicker _picker = Substitute.For<IFilePicker>();
    private readonly IFolderLauncher _launcher = Substitute.For<IFolderLauncher>();
    private readonly IToastService _toasts = Substitute.For<IToastService>();
    private readonly SettingsViewModel _viewModel;

    public SettingsDiagnosticsTests()
    {
        var strings = Substitute.For<IStringResourceProvider>();
        strings.GetString(Arg.Any<string>()).Returns(call => call.Arg<string>() + "|{0}");
        _viewModel = new SettingsViewModel(
            Substitute.For<ISettingsStore>(), _toasts, strings, _exporter, _picker, _launcher,
            new FixedClock(new DateTimeOffset(2026, 10, 3, 9, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public async Task The_save_dialog_is_offered_a_dated_default_name()
    {
        _picker.PickSaveZipAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((string?)null);

        await _viewModel.SaveDiagnosticsCommand.ExecuteAsync(null);

        await _picker.Received(1).PickSaveZipAsync("dna-entropy-diagnostics-2026-10-03.zip", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Cancelling_the_picker_builds_nothing_and_says_nothing()
    {
        _picker.PickSaveZipAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((string?)null);

        await _viewModel.SaveDiagnosticsCommand.ExecuteAsync(null);

        await _exporter.DidNotReceiveWithAnyArgs().ExportAsync(default!, TestContext.Current.CancellationToken);
        _toasts.DidNotReceiveWithAnyArgs().ShowToast(default!, default!);
        _viewModel.DiagnosticsStatus.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_save_runs_the_export_off_the_calling_thread_then_toasts_and_enables_open_folder()
    {
        _picker.PickSaveZipAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(@"C:\Out\d.zip");
        var callerThread = -1;
        int? exportThread = null;
        // A synchronous export body: if the command awaited it on the caller's thread it would run there. Task.Run moves it.
        _exporter.ExportAsync(@"C:\Out\d.zip", Arg.Any<CancellationToken>()).Returns(_ =>
        {
            exportThread = Environment.CurrentManagedThreadId;
            return Task.CompletedTask;
        });

        _viewModel.OpenDiagnosticsFolderCommand.CanExecute(null).ShouldBeFalse("nothing is saved yet");
        // A dedicated thread that blocks until the command ends: no other work can reuse it, so a different id proves Task.Run.
        var caller = new Thread(() =>
        {
            callerThread = Environment.CurrentManagedThreadId;
            _viewModel.SaveDiagnosticsCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        });
        caller.Start();
        caller.Join();

        exportThread.ShouldNotBeNull();
        exportThread.ShouldNotBe(callerThread);
        _toasts.Received(1).ShowToast("DiagnosticsSaved_Title|{0}", Arg.Is<string>(b => b.Contains(@"C:\Out\d.zip")));
        _viewModel.DiagnosticsStatus.ShouldContain(@"C:\Out\d.zip");
        _viewModel.OpenDiagnosticsFolderCommand.CanExecute(null).ShouldBeTrue();

        _viewModel.OpenDiagnosticsFolderCommand.Execute(null);
        _launcher.Received(1).RevealFile(@"C:\Out\d.zip");
    }

    [Fact]
    public async Task A_refused_bundle_toasts_the_refusal_copy_and_does_not_offer_open_folder()
    {
        _picker.PickSaveZipAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(@"C:\Out\d.zip");
        _exporter.ExportAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromException(new DiagnosticsLeakException("run-history.json")));

        await _viewModel.SaveDiagnosticsCommand.ExecuteAsync(null);

        _toasts.Received(1).ShowToast("DiagnosticsSaveFailed_Title|{0}", "DiagnosticsSaveFailed_Refused|{0}");
        _viewModel.OpenDiagnosticsFolderCommand.CanExecute(null).ShouldBeFalse();
        _viewModel.IsSavingDiagnostics.ShouldBeFalse();
    }

    [Fact]
    public async Task A_disk_failure_toasts_the_write_copy_and_the_command_can_run_again()
    {
        _picker.PickSaveZipAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(@"C:\Out\d.zip");
        _exporter.ExportAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromException(new IOException("disk full")));

        await _viewModel.SaveDiagnosticsCommand.ExecuteAsync(null);

        _toasts.Received(1).ShowToast("DiagnosticsSaveFailed_Title|{0}", "DiagnosticsSaveFailed_Write|{0}");
        _viewModel.SaveDiagnosticsCommand.CanExecute(null).ShouldBeTrue();
    }

    [Fact]
    public void The_button_label_is_the_wording_the_error_strings_use()
    {
        SettingsViewModel.SaveDiagnosticsLabel.ShouldBe("Save diagnostics");
    }
}
