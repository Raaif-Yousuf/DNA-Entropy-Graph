using CommunityToolkit.Mvvm.Messaging;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Presentation.Messaging;
using DnaEntropyGraph.Presentation.Services;
using DnaEntropyGraph.Presentation.ViewModels;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

/// <summary>Issue #643: the Settings Notifications group and the completion path that reads it.</summary>
public sealed class NotificationsTests
{
    private readonly MemorySettings _settings = new();
    private readonly IToastService _toasts = Substitute.For<IToastService>();
    private readonly IFolderLauncher _folders = Substitute.For<IFolderLauncher>();
    private readonly INavigator _navigator = Substitute.For<INavigator>();
    private readonly IRunRepository _runs = Substitute.For<IRunRepository>();
    private readonly IStringResourceProvider _strings = Substitute.For<IStringResourceProvider>();

    public NotificationsTests()
    {
        _strings.GetString(Arg.Any<string>()).Returns(call => call.Arg<string>());
        _folders.OpenFolder(Arg.Any<string>()).Returns(true);
        _runs.GetAllAsync(Arg.Any<CancellationToken>()).Returns(
            Task.FromResult<IReadOnlyList<RunRecord>>([new RunRecord("job-1", JobPhase.Completed, DateTimeOffset.UnixEpoch, OutputDir: @"C:\out\job-1")]));
    }

    private RunCompletionActions Actions() => new(new NotificationSettings(_settings), _toasts, _folders, _navigator, _runs, _strings, new InlineDispatcher());

    private static async Task Finish(RunCompletionActions actions, JobPhase phase = JobPhase.Completed)
        => await actions.OnRunFinishedAsync("job-1", phase);

    [Fact]
    public void Defaults_are_notify_on_open_folder_off_open_results_on()
    {
        var settings = new NotificationSettings(_settings);

        settings.NotifyWhenDone.ShouldBeTrue();
        settings.OpenFolderWhenDone.ShouldBeFalse();
        settings.OpenResultsWhenDone.ShouldBeTrue();
    }

    [Fact]
    public void A_saved_value_is_read_back_and_a_locked_file_reads_as_the_defaults()
    {
        var settings = new NotificationSettings(_settings);
        settings.OpenResultsWhenDone = false;
        settings.OpenFolderWhenDone = true;

        new NotificationSettings(_settings).OpenResultsWhenDone.ShouldBeFalse();
        new NotificationSettings(_settings).OpenFolderWhenDone.ShouldBeTrue();

        _settings.Unavailable = true;
        new NotificationSettings(_settings).OpenResultsWhenDone.ShouldBeTrue();
    }

    [Fact]
    public async Task A_finished_run_with_the_defaults_shows_one_toast_and_opens_Results_but_not_the_folder()
    {
        await Finish(Actions());

        _toasts.Received(1).ShowToast("RunDone_Title", "RunDone_Body", ToastSeverity.Success);
        _navigator.Received(1).NavigateTo(ResultsViewModel.PageKey, "job-1");
        _folders.DidNotReceive().OpenFolder(Arg.Any<string>());
    }

    [Fact]
    public async Task Turning_off_Notify_when_done_means_no_toast()
    {
        new NotificationSettings(_settings).NotifyWhenDone = false;

        await Finish(Actions());

        _toasts.DidNotReceiveWithAnyArgs().ShowToast(default!, default!, default);
        _navigator.Received(1).NavigateTo(ResultsViewModel.PageKey, "job-1");
    }

    [Fact]
    public async Task Turning_off_Open_results_when_done_leaves_the_user_on_the_current_page()
    {
        new NotificationSettings(_settings).OpenResultsWhenDone = false;

        await Finish(Actions());

        _navigator.DidNotReceiveWithAnyArgs().NavigateTo(default!, default);
        _toasts.Received(1).ShowToast("RunDone_Title", "RunDone_Body", ToastSeverity.Success);
    }

    [Fact]
    public async Task Turning_on_Open_the_output_folder_opens_the_folder_the_run_downloaded_to()
    {
        new NotificationSettings(_settings).OpenFolderWhenDone = true;

        await Finish(Actions());

        _folders.Received(1).OpenFolder(@"C:\out\job-1");
    }

    [Fact]
    public async Task A_run_with_no_known_output_folder_does_not_try_to_open_one()
    {
        new NotificationSettings(_settings).OpenFolderWhenDone = true;
        _runs.GetAllAsync(Arg.Any<CancellationToken>()).Returns(
            Task.FromResult<IReadOnlyList<RunRecord>>([new RunRecord("job-1", JobPhase.Completed, DateTimeOffset.UnixEpoch)]));

        await Finish(Actions());

        _folders.DidNotReceive().OpenFolder(Arg.Any<string>());
    }

    [Fact]
    public async Task A_partly_finished_run_says_so_in_its_toast()
    {
        await Finish(Actions(), JobPhase.PartiallyCompleted);

        _toasts.Received(1).ShowToast("RunPartial_Title", "RunPartial_Body", ToastSeverity.Warning);
    }

    [Fact]
    public async Task A_repository_that_throws_never_stops_the_toast_or_Results()
    {
        new NotificationSettings(_settings).OpenFolderWhenDone = true;
        _runs.GetAllAsync(Arg.Any<CancellationToken>()).Returns<IReadOnlyList<RunRecord>>(_ => throw new IOException("db"));

        await Finish(Actions());

        _toasts.Received(1).ShowToast(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ToastSeverity>());
        _navigator.Received(1).NavigateTo(ResultsViewModel.PageKey, "job-1");
    }

    [Theory]
    [InlineData(JobPhase.Failed)]
    [InlineData(JobPhase.Cancelled)]
    [InlineData(JobPhase.Running)]
    public void Only_a_completed_or_partly_completed_run_triggers_anything_through_the_shell(JobPhase phase)
    {
        var messenger = new WeakReferenceMessenger();
        var shell = new ShellViewModel(_navigator, Substitute.For<IGcpAccount>(), messenger, _strings, TestMessages.Center(), completion: Actions());

        messenger.Send(new RunPhaseChangedMessage("job-1", phase));

        _toasts.DidNotReceiveWithAnyArgs().ShowToast(default!, default!, default);
        _navigator.DidNotReceiveWithAnyArgs().NavigateTo(default!, default);
        shell.ShouldNotBeNull();
    }

    [Fact]
    public async Task The_shell_runs_the_completion_actions_when_a_run_finishes()
    {
        var messenger = new WeakReferenceMessenger();
        var shell = new ShellViewModel(_navigator, Substitute.For<IGcpAccount>(), messenger, _strings, TestMessages.Center(), completion: Actions());

        messenger.Send(new RunPhaseChangedMessage("job-1", JobPhase.Completed));
        await shell.CompletionWork;

        _toasts.Received(1).ShowToast("RunDone_Title", "RunDone_Body", ToastSeverity.Success);
        _navigator.Received(1).NavigateTo(ResultsViewModel.PageKey, "job-1");
    }

    [Fact]
    public void The_settings_group_saves_each_switch_to_the_store_the_completion_path_reads()
    {
        var vm = new NotificationsSettingsViewModel(new NotificationSettings(_settings), _strings);

        vm.NotifyWhenDone.ShouldBeTrue();
        vm.OpenFolderWhenDone.ShouldBeFalse();
        vm.OpenResultsWhenDone.ShouldBeTrue();

        vm.NotifyWhenDone = false;
        vm.OpenFolderWhenDone = true;
        vm.OpenResultsWhenDone = false;

        var read = new NotificationSettings(_settings);
        read.NotifyWhenDone.ShouldBeFalse();
        read.OpenFolderWhenDone.ShouldBeTrue();
        read.OpenResultsWhenDone.ShouldBeFalse();
        vm.NotificationsStatus.ShouldBeEmpty();
    }

    [Fact]
    public void Opening_the_group_saves_nothing()
    {
        _ = new NotificationsSettingsViewModel(new NotificationSettings(_settings), _strings);

        _settings.Values.ShouldBeEmpty();
    }

    [Fact]
    public void A_locked_settings_file_is_reported_with_the_action_and_a_later_save_clears_it()
    {
        var vm = new NotificationsSettingsViewModel(new NotificationSettings(_settings), _strings);
        _settings.Unavailable = true;

        vm.OpenFolderWhenDone = true;
        vm.NotificationsStatus.ShouldBe("NotificationsNotSaved");

        _settings.Unavailable = false;
        vm.OpenFolderWhenDone = false;
        vm.NotificationsStatus.ShouldBeEmpty();
    }

    private sealed class InlineDispatcher : IDispatcher
    {
        public void Enqueue(Action action) => action();
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
