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
    private readonly IDesktopNotifier _desktop = Substitute.For<IDesktopNotifier>();

    public NotificationsTests()
    {
        _strings.GetString(Arg.Any<string>()).Returns(call => call.Arg<string>());
        _folders.OpenFolder(Arg.Any<string>()).Returns(true);
        _runs.GetAllAsync(Arg.Any<CancellationToken>()).Returns(
            Task.FromResult<IReadOnlyList<RunRecord>>([new RunRecord("job-1", JobPhase.Completed, DateTimeOffset.UnixEpoch, OutputDir: @"C:\out\job-1")]));
    }

    private RunCompletionActions Actions() => new(new NotificationSettings(_settings), _toasts, _folders, _navigator, _runs, _strings, new InlineDispatcher(), _desktop);

    private static async Task Finish(RunCompletionActions actions, JobPhase phase = JobPhase.Completed)
        => await actions.OnRunFinishedAsync("job-1", phase);

    [Fact]
    public void Defaults_are_notify_on_open_folder_off_open_results_off()
    {
        var settings = new NotificationSettings(_settings);

        settings.NotifyWhenDone.ShouldBeTrue();
        settings.OpenFolderWhenDone.ShouldBeFalse();
        settings.OpenResultsWhenDone.ShouldBeFalse();
    }

    [Fact]
    public void A_saved_value_is_read_back_and_a_locked_file_reads_as_the_defaults()
    {
        var settings = new NotificationSettings(_settings);
        settings.OpenResultsWhenDone = true;
        settings.OpenFolderWhenDone = true;

        new NotificationSettings(_settings).OpenResultsWhenDone.ShouldBeTrue();
        new NotificationSettings(_settings).OpenFolderWhenDone.ShouldBeTrue();

        _settings.Unavailable = true;
        new NotificationSettings(_settings).OpenResultsWhenDone.ShouldBeFalse();
    }

    [Fact]
    public async Task A_finished_run_with_the_defaults_shows_one_toast_and_leaves_the_user_where_they_are()
    {
        // #643 cold review (4): a page that jumps away while the user is mid-edit on New run loses their place, so it is opt-in.
        await Finish(Actions());

        _toasts.Received(1).ShowToast("RunDone_Title", "RunDone_Body", ToastSeverity.Success);
        _navigator.DidNotReceiveWithAnyArgs().NavigateTo(default!, default);
        _folders.DidNotReceive().OpenFolder(Arg.Any<string>());
    }

    [Fact]
    public async Task Turning_off_Notify_when_done_means_no_toast()
    {
        new NotificationSettings(_settings).NotifyWhenDone = false;
        new NotificationSettings(_settings).OpenResultsWhenDone = true;

        await Finish(Actions());

        _toasts.DidNotReceiveWithAnyArgs().ShowToast(default!, default!, default);
        _navigator.Received(1).NavigateTo(ResultsViewModel.PageKey, "job-1");
    }

    [Fact]
    public async Task Turning_on_Open_results_when_done_opens_the_Results_page_for_that_run()
    {
        new NotificationSettings(_settings).OpenResultsWhenDone = true;

        await Finish(Actions());

        _navigator.Received(1).NavigateTo(ResultsViewModel.PageKey, "job-1");
        _toasts.Received(1).ShowToast("RunDone_Title", "RunDone_Body", ToastSeverity.Success);
    }

    [Fact]
    public async Task The_settings_are_read_once_and_off_the_thread_that_reported_the_run_finished()
    {
        // #643 cold review (6): the settings store is a mutex with a 300 ms budget; the run-driving thread must not wait on it.
        var callerThread = Environment.CurrentManagedThreadId;
        var store = new ThreadRecordingSettings(_settings);

        await new RunCompletionActions(new NotificationSettings(store), _toasts, _folders, _navigator, _runs, _strings, new InlineDispatcher())
            .OnRunFinishedAsync("job-1", JobPhase.Completed);

        store.ReadThreads.ShouldNotBeEmpty();
        store.ReadThreads.ShouldAllBe(t => t != callerThread);
        store.ReadThreads.Count.ShouldBe(3, "one read per switch, not one per use");
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
        new NotificationSettings(_settings).OpenResultsWhenDone = true;
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
        var shell = new ShellViewModel(_navigator, Substitute.For<IGcpAccount>(), messenger, _strings, TestMessages.Center(), Actions());

        messenger.Send(new RunPhaseChangedMessage("job-1", phase));

        _toasts.DidNotReceiveWithAnyArgs().ShowToast(default!, default!, default);
        _navigator.DidNotReceiveWithAnyArgs().NavigateTo(default!, default);
        shell.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_finished_run_also_goes_to_the_desktop_notifier_with_the_run_to_open()
    {
        await Finish(Actions());

        _desktop.Received(1).Notify("RunDone_Title", "RunDone_Body", "job-1");
    }

    [Fact]
    public async Task A_partly_finished_run_is_a_desktop_notification_too()
    {
        await Finish(Actions(), JobPhase.PartiallyCompleted);

        _desktop.Received(1).Notify("RunPartial_Title", "RunPartial_Body", "job-1");
    }

    [Fact]
    public async Task Turning_off_Notify_when_done_means_no_desktop_notification_either()
    {
        new NotificationSettings(_settings).NotifyWhenDone = false;

        await Finish(Actions());
        await Actions().OnRunFailedAsync("job-1");

        _desktop.DidNotReceiveWithAnyArgs().Notify(default!, default!, default);
    }

    [Fact]
    public async Task A_failed_run_is_a_desktop_notification_only_and_never_opens_a_page_or_a_folder()
    {
        new NotificationSettings(_settings).OpenResultsWhenDone = true;
        new NotificationSettings(_settings).OpenFolderWhenDone = true;

        await Actions().OnRunFailedAsync("job-1");

        _desktop.Received(1).Notify("RunFailed_Title", "RunFailed_Body", "job-1");
        _toasts.DidNotReceiveWithAnyArgs().ShowToast(default!, default!, default);
        _navigator.DidNotReceiveWithAnyArgs().NavigateTo(default!, default);
        _folders.DidNotReceive().OpenFolder(Arg.Any<string>());
    }

    [Fact]
    public async Task A_desktop_notifier_that_throws_never_stops_the_in_app_toast_or_Results()
    {
        new NotificationSettings(_settings).OpenResultsWhenDone = true;
        _desktop.When(d => d.Notify(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>())).Do(_ => throw new InvalidOperationException("no toast"));

        await Finish(Actions());

        _toasts.Received(1).ShowToast("RunDone_Title", "RunDone_Body", ToastSeverity.Success);
        _navigator.Received(1).NavigateTo(ResultsViewModel.PageKey, "job-1");
    }

    [Fact]
    public async Task The_shell_sends_a_failed_run_to_the_desktop_notifier()
    {
        var messenger = new WeakReferenceMessenger();
        var shell = new ShellViewModel(_navigator, Substitute.For<IGcpAccount>(), messenger, _strings, TestMessages.Center(), Actions());

        messenger.Send(new RunPhaseChangedMessage("job-1", JobPhase.Failed));
        await shell.CompletionWork;

        _desktop.Received(1).Notify("RunFailed_Title", "RunFailed_Body", "job-1");
    }

    [Fact]
    public async Task The_shell_runs_the_completion_actions_when_a_run_finishes()
    {
        new NotificationSettings(_settings).OpenResultsWhenDone = true;
        var messenger = new WeakReferenceMessenger();
        var shell = new ShellViewModel(_navigator, Substitute.For<IGcpAccount>(), messenger, _strings, TestMessages.Center(), Actions());

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
        vm.OpenResultsWhenDone.ShouldBeFalse();

        vm.NotifyWhenDone = false;
        vm.OpenFolderWhenDone = true;
        vm.OpenResultsWhenDone = true;

        var read = new NotificationSettings(_settings);
        read.NotifyWhenDone.ShouldBeFalse();
        read.OpenFolderWhenDone.ShouldBeTrue();
        read.OpenResultsWhenDone.ShouldBeTrue();
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

    private sealed class ThreadRecordingSettings(ISettingsStore inner) : ISettingsStore
    {
        public List<int> ReadThreads { get; } = [];

        public string? GetString(string key)
        {
            lock (ReadThreads)
            {
                ReadThreads.Add(Environment.CurrentManagedThreadId);
            }

            return inner.GetString(key);
        }

        public void SetString(string key, string value) => inner.SetString(key, value);
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
