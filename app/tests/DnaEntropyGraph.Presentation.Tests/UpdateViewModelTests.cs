using CommunityToolkit.Mvvm.Messaging;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Presentation.Messaging;
using DnaEntropyGraph.Presentation.Services;
using DnaEntropyGraph.Presentation.ViewModels;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

/// <summary>Issues #179 and #145: the in-app update check, its channel, and the rule that an update never restarts the app mid-run.</summary>
public sealed class UpdateViewModelTests
{
    private readonly FakeUpdateSource _source = new();
    private readonly MemorySettings _store = new();
    private readonly IStringResourceProvider _strings = Substitute.For<IStringResourceProvider>();
    private readonly IActiveRunProbe _runs = Substitute.For<IActiveRunProbe>();
    private readonly IMessenger _messenger = new WeakReferenceMessenger();

    public UpdateViewModelTests()
    {
        _strings.GetString(Arg.Any<string>()).Returns(call => call.Arg<string>());
    }

    private readonly ManualDispatcher _dispatcher = new();

    private UpdateViewModel Vm(TimeSpan? checkTimeout = null) => new(_source, new UpdateSettings(_store), _strings, _runs, _messenger, _dispatcher, checkTimeout);

    [Fact]
    public void Defaults_are_the_stable_channel_and_check_on_launch()
    {
        var settings = new UpdateSettings(_store);

        settings.Channel.ShouldBe(UpdateChannel.Stable);
        settings.CheckOnLaunch.ShouldBeTrue();
        Vm().ChannelIndex.ShouldBe(0);
    }

    [Fact]
    public void A_saved_channel_and_launch_switch_are_read_back_and_a_locked_file_reads_as_the_defaults()
    {
        var settings = new UpdateSettings(_store) { Channel = UpdateChannel.Beta, CheckOnLaunch = false };

        new UpdateSettings(_store).Channel.ShouldBe(UpdateChannel.Beta);
        new UpdateSettings(_store).CheckOnLaunch.ShouldBeFalse();
        Vm().ChannelIndex.ShouldBe(1);

        _store.Unavailable = true;
        settings.Channel.ShouldBe(UpdateChannel.Stable);
        settings.CheckOnLaunch.ShouldBeTrue();
    }

    [Fact]
    public async Task Check_now_with_a_newer_release_opens_the_banner_with_the_version_and_the_notes()
    {
        _source.Result = new UpdateCheckResult(UpdateCheckOutcome.Available, new AvailableUpdate("1.2.0", "Fixes a crash."));
        var vm = Vm();

        await vm.CheckNowCommand.ExecuteAsync(null);

        vm.IsBannerOpen.ShouldBeTrue();
        vm.HasUpdate.ShouldBeTrue();
        vm.AvailableVersion.ShouldBe("1.2.0");
        vm.ReleaseNotes.ShouldBe("Fixes a crash.");
        vm.StatusText.ShouldContain("UpdateAvailable_Status");
        _source.Applied.ShouldBe(0);
    }

    [Fact]
    public async Task Check_now_with_nothing_newer_says_up_to_date_and_shows_no_banner()
    {
        _source.Result = new UpdateCheckResult(UpdateCheckOutcome.UpToDate);
        var vm = Vm();

        await vm.CheckNowCommand.ExecuteAsync(null);

        vm.IsBannerOpen.ShouldBeFalse();
        vm.HasUpdate.ShouldBeFalse();
        vm.StatusText.ShouldBe("UpdateUpToDate");
    }

    [Fact]
    public async Task A_copy_that_is_not_installed_says_so_instead_of_failing()
    {
        _source.Result = new UpdateCheckResult(UpdateCheckOutcome.NotInstalled);
        var vm = Vm();

        await vm.CheckNowCommand.ExecuteAsync(null);

        vm.StatusText.ShouldBe("UpdateNotInstalled");
        vm.IsBannerOpen.ShouldBeFalse();
    }

    [Fact]
    public async Task A_failed_check_by_the_user_names_the_action_and_a_failed_launch_check_stays_silent()
    {
        _source.Throw = new HttpRequestException("offline");
        var vm = Vm();

        await vm.CheckOnLaunchAsync();
        vm.StatusText.ShouldBeEmpty();
        vm.IsBannerOpen.ShouldBeFalse();

        await vm.CheckNowCommand.ExecuteAsync(null);
        vm.StatusText.ShouldBe("UpdateCheckFailed");
    }

    [Fact]
    public async Task The_channel_picked_in_settings_is_what_the_source_is_asked_for_and_is_saved()
    {
        var vm = Vm();

        vm.ChannelIndex = 1;
        await vm.CheckNowCommand.ExecuteAsync(null);

        _source.ChannelsAsked.ShouldBe([UpdateChannel.Beta]);
        new UpdateSettings(_store).Channel.ShouldBe(UpdateChannel.Beta);
    }

    [Fact]
    public async Task Switching_the_channel_drops_an_update_offered_on_the_other_one()
    {
        _source.Result = new UpdateCheckResult(UpdateCheckOutcome.Available, new AvailableUpdate("1.3.0-beta.1", null));
        var vm = Vm();
        vm.ChannelIndex = 1;
        await vm.CheckNowCommand.ExecuteAsync(null);
        vm.HasUpdate.ShouldBeTrue();

        vm.ChannelIndex = 0;

        vm.HasUpdate.ShouldBeFalse();
        vm.IsBannerOpen.ShouldBeFalse();
    }

    [Fact]
    public async Task Check_on_launch_does_nothing_when_the_switch_is_off()
    {
        new UpdateSettings(_store).CheckOnLaunch = false;
        var vm = Vm();

        await vm.CheckOnLaunchAsync();

        _source.ChannelsAsked.ShouldBeEmpty();
    }

    [Fact]
    public async Task Check_on_launch_with_the_switch_on_opens_the_banner()
    {
        _source.Result = new UpdateCheckResult(UpdateCheckOutcome.Available, new AvailableUpdate("1.2.0", null));
        var vm = Vm();

        await vm.CheckOnLaunchAsync();

        vm.IsBannerOpen.ShouldBeTrue();
    }

    [Fact]
    public async Task Restart_to_update_downloads_then_applies_when_no_run_is_active()
    {
        _source.Result = new UpdateCheckResult(UpdateCheckOutcome.Available, new AvailableUpdate("1.2.0", null));
        var vm = Vm();
        await vm.CheckNowCommand.ExecuteAsync(null);

        await vm.RestartToUpdateCommand.ExecuteAsync(null);

        _source.Downloaded.ShouldBe(1);
        _source.Applied.ShouldBe(1);
    }

    [Fact]
    public async Task Restart_to_update_never_downloads_or_applies_while_a_run_is_active()
    {
        _source.Result = new UpdateCheckResult(UpdateCheckOutcome.Available, new AvailableUpdate("1.2.0", null));
        _runs.AnyActive.Returns(true);
        var vm = Vm();
        await vm.CheckNowCommand.ExecuteAsync(null);

        await vm.RestartToUpdateCommand.ExecuteAsync(null);

        _source.Applied.ShouldBe(0);
        vm.StatusText.ShouldBe("UpdateWaitForRun");
        vm.HasUpdate.ShouldBeTrue();
    }

    [Fact]
    public async Task A_run_that_starts_during_the_download_stops_the_restart()
    {
        _source.Result = new UpdateCheckResult(UpdateCheckOutcome.Available, new AvailableUpdate("1.2.0", null));
        var vm = Vm();
        await vm.CheckNowCommand.ExecuteAsync(null);
        _source.OnDownload = () => _runs.AnyActive.Returns(true);

        await vm.RestartToUpdateCommand.ExecuteAsync(null);

        _source.Downloaded.ShouldBe(1);
        _source.Applied.ShouldBe(0);
        vm.StatusText.ShouldBe("UpdateWaitForRun");

        // The user presses it again once the run is over: the download is kept, so it applies.
        _runs.AnyActive.Returns(false);
        await vm.RestartToUpdateCommand.ExecuteAsync(null);
        _source.Applied.ShouldBe(1);
    }

    [Fact]
    public async Task A_failed_download_names_the_action_and_does_not_apply()
    {
        _source.Result = new UpdateCheckResult(UpdateCheckOutcome.Available, new AvailableUpdate("1.2.0", null));
        var vm = Vm();
        await vm.CheckNowCommand.ExecuteAsync(null);
        _source.OnDownload = () => throw new IOException("disk");

        await vm.RestartToUpdateCommand.ExecuteAsync(null);

        _source.Applied.ShouldBe(0);
        vm.StatusText.ShouldBe("UpdateDownloadFailed");
        vm.IsBusy.ShouldBeFalse();
    }

    [Fact]
    public async Task Download_progress_reported_from_another_thread_reaches_status_text_only_through_the_dispatcher()
    {
        _source.Result = new UpdateCheckResult(UpdateCheckOutcome.Available, new AvailableUpdate("1.2.0", null));
        _strings.GetString("UpdateDownloadingPercent").Returns("Downloading {0}%");
        var vm = Vm();
        await vm.CheckNowCommand.ExecuteAsync(null);
        var seenRightAfter = string.Empty;
        _source.OnProgress = progress =>
        {
            Task.Run(() => progress(42)).Wait();
            seenRightAfter = vm.StatusText;
        };

        await vm.RestartToUpdateCommand.ExecuteAsync(null);

        // Nothing wrote StatusText on the worker thread: the percent waits for the UI thread.
        seenRightAfter.ShouldNotContain("42");
        _dispatcher.RunAll();
        vm.StatusText.ShouldBe("Downloading 42%");
    }

    [Fact]
    public async Task A_pre_release_version_is_refused_on_stable_even_when_the_feed_offers_it_and_accepted_on_beta()
    {
        _source.Result = new UpdateCheckResult(UpdateCheckOutcome.Available, new AvailableUpdate("1.3.0-beta.1", null));
        var vm = Vm();

        await vm.CheckNowCommand.ExecuteAsync(null);
        vm.HasUpdate.ShouldBeFalse();
        vm.IsBannerOpen.ShouldBeFalse();
        vm.StatusText.ShouldBe("UpdateUpToDate");

        vm.ChannelIndex = 1;
        await vm.CheckNowCommand.ExecuteAsync(null);
        vm.HasUpdate.ShouldBeTrue();
    }

    [Theory]
    [InlineData("1.2.0", true)]
    [InlineData("1.2.0+build5", true)]
    [InlineData("1.2.0-rc.1", false)]
    [InlineData("1.2.0-beta.1+sha", false)]
    public void Stable_allows_only_a_version_without_a_pre_release_tag(string version, bool allowed)
    {
        UpdateChannelPolicy.Allows(UpdateChannel.Stable, version).ShouldBe(allowed);
        UpdateChannelPolicy.Allows(UpdateChannel.Beta, version).ShouldBeTrue();
    }

    [Fact]
    public async Task A_check_that_never_answers_gives_up_at_the_deadline_and_names_the_action()
    {
        _source.Hang = true;
        var vm = Vm(TimeSpan.FromMilliseconds(50));

        await vm.CheckNowCommand.ExecuteAsync(null);

        vm.StatusText.ShouldBe("UpdateCheckFailed");
        vm.IsBusy.ShouldBeFalse();
    }

    [Fact]
    public async Task Reset_all_settings_puts_the_channel_and_switch_back_without_saving()
    {
        var vm = Vm();
        vm.ChannelIndex = 1;
        vm.CheckOnLaunch = false;
        _store.Values.Clear();

        _messenger.Send(new SettingsResetMessage());

        vm.ChannelIndex.ShouldBe(0);
        vm.CheckOnLaunch.ShouldBeTrue();
        _store.Values.ShouldBeEmpty();
        await Task.CompletedTask;
    }

    [Fact]
    public void The_current_version_is_shown_in_plain_words()
    {
        _strings.GetString("AboutVersion_Text").Returns("Version {0}");
        _source.CurrentVersion = "1.0.4";

        Vm().CurrentVersionText.ShouldContain("1.0.4");
    }

    private sealed class FakeUpdateSource : IAppUpdateSource
    {
        public UpdateCheckResult Result { get; set; } = new(UpdateCheckOutcome.UpToDate);

        public Exception? Throw { get; set; }

        public Action? OnDownload { get; set; }

        public bool Hang { get; set; }

        public Action<Action<int>>? OnProgress { get; set; }

        public List<UpdateChannel> ChannelsAsked { get; } = [];

        public int Downloaded { get; private set; }

        public int Applied { get; private set; }

        public string CurrentVersion { get; set; } = "1.0.0";

        public Task<UpdateCheckResult> CheckAsync(UpdateChannel channel, CancellationToken cancellationToken)
        {
            ChannelsAsked.Add(channel);
            if (Hang)
            {
                return Task.Delay(Timeout.Infinite, cancellationToken).ContinueWith<UpdateCheckResult>(_ => throw new InvalidOperationException("unreachable"), TaskScheduler.Default);
            }

            return Throw is null ? Task.FromResult(Result) : Task.FromException<UpdateCheckResult>(Throw);
        }

        public Task DownloadAsync(Action<int>? progress, CancellationToken cancellationToken)
        {
            Downloaded++;
            OnDownload?.Invoke();
            OnProgress?.Invoke(percent => progress?.Invoke(percent));
            return Task.CompletedTask;
        }

        public void ApplyAndRestart() => Applied++;
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

