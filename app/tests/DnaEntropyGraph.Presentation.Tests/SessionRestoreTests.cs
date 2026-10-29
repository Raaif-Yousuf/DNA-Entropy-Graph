using CommunityToolkit.Mvvm.Messaging;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Presentation.Services;
using DnaEntropyGraph.Presentation.ViewModels;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

/// <summary>Issue #244: the app reopens on the page and run the user left. A run that finished while the app was closed opens on Results.</summary>
public sealed class SessionRestoreTests
{
    private readonly MemorySettings _settings = new();
    private readonly IRunRepository _runs = Substitute.For<IRunRepository>();
    private readonly INavigator _inner = Substitute.For<INavigator>();

    private sealed class MemorySettings : ISettingsStore
    {
        public Dictionary<string, string> Values { get; } = [];

        public bool Unavailable { get; set; }

        public string? GetString(string key) => Unavailable ? throw new SettingsUnavailableException("locked") : Values.GetValueOrDefault(key);

        public void SetString(string key, string value)
        {
            if (Unavailable)
            {
                throw new SettingsUnavailableException("locked");
            }

            Values[key] = value;
        }
    }

    private SessionStore Store() => new(_settings);

    private void HaveRuns(params (string Id, JobPhase Phase)[] runs)
        => _runs.GetAllAsync(Arg.Any<CancellationToken>()).Returns(runs.Select(r => new RunRecord(r.Id, r.Phase, DateTimeOffset.UtcNow)).ToList());

    // ---- recording ----

    [Theory]
    [InlineData("NewRun")]
    [InlineData("Runs")]
    [InlineData("Cloud")]
    [InlineData("Settings")]
    public void A_top_level_page_is_remembered_with_no_run(string page)
    {
        var navigator = new RecordingNavigator(_inner, Store());

        navigator.NavigateTo("Results", "job-1");
        navigator.NavigateTo(page);

        Store().Load().ShouldBe(new SessionState(page, null));
        _inner.Received(1).NavigateTo(page, null);
    }

    [Theory]
    [InlineData("Results")]
    [InlineData("RunProgress")]
    public void A_run_page_is_remembered_with_its_run(string page)
    {
        new RecordingNavigator(_inner, Store()).NavigateTo(page, "job-42");

        Store().Load().ShouldBe(new SessionState(page, "job-42"));
        _inner.Received(1).NavigateTo(page, "job-42");
    }

    [Theory]
    [InlineData("Viewer", "C:\\some\\folder")]
    [InlineData("Wizard", null)]
    [InlineData("Whatever", null)]
    public void A_page_that_cannot_be_reopened_leaves_the_last_remembered_page_alone_but_still_navigates(string page, string? parameter)
    {
        var navigator = new RecordingNavigator(_inner, Store());
        navigator.NavigateTo("Results", "job-1");

        navigator.NavigateTo(page, parameter);

        Store().Load().ShouldBe(new SessionState("Results", "job-1"));
        _inner.Received(1).NavigateTo(page, parameter);
    }

    [Fact]
    public void A_locked_settings_file_never_stops_navigation()
    {
        _settings.Unavailable = true;
        var navigator = new RecordingNavigator(_inner, Store());

        Should.NotThrow(() => navigator.NavigateTo("Runs"));

        _inner.Received(1).NavigateTo("Runs", null);
        Store().Load().ShouldBeNull();
    }

    [Fact]
    public void Back_and_can_go_back_pass_through()
    {
        _inner.CanGoBack.Returns(true);
        var navigator = new RecordingNavigator(_inner, Store());

        navigator.CanGoBack.ShouldBeTrue();
        navigator.GoBack();

        _inner.Received(1).GoBack();
    }

    // ---- resolving ----

    [Fact]
    public async Task Nothing_remembered_means_no_restore()
    {
        HaveRuns();
        (await new SessionRestorer(Store(), _runs).ResolveAsync(CancellationToken.None)).ShouldBeNull();
    }

    [Theory]
    [InlineData("Runs")]
    [InlineData("Cloud")]
    [InlineData("Settings")]
    public async Task A_remembered_top_level_page_is_reopened(string page)
    {
        HaveRuns();
        Store().Save(new SessionState(page, null));

        var target = await new SessionRestorer(Store(), _runs).ResolveAsync(CancellationToken.None);

        target.ShouldBe(new SessionTarget(page, null, null));
    }

    [Fact]
    public async Task Results_of_an_existing_run_reopens_that_run_under_Runs()
    {
        HaveRuns(("job-1", JobPhase.Completed));
        Store().Save(new SessionState("Results", "job-1"));

        var target = await new SessionRestorer(Store(), _runs).ResolveAsync(CancellationToken.None);

        target.ShouldBe(new SessionTarget("Runs", "Results", "job-1"));
    }

    [Fact]
    public async Task Results_of_a_run_that_is_gone_falls_back_to_the_default_page()
    {
        HaveRuns(("other", JobPhase.Completed));
        Store().Save(new SessionState("Results", "job-1"));

        (await new SessionRestorer(Store(), _runs).ResolveAsync(CancellationToken.None)).ShouldBeNull();
    }

    [Theory]
    [InlineData(JobPhase.Running)]
    [InlineData(JobPhase.Provisioning)]
    [InlineData(JobPhase.Failed)]
    [InlineData(JobPhase.Cancelled)]
    public async Task A_run_page_for_an_unfinished_or_failed_run_reopens_its_progress_page(JobPhase phase)
    {
        HaveRuns(("job-1", phase));
        Store().Save(new SessionState("RunProgress", "job-1"));

        var target = await new SessionRestorer(Store(), _runs).ResolveAsync(CancellationToken.None);

        target.ShouldBe(new SessionTarget("Runs", "RunProgress", "job-1"));
    }

    [Theory]
    [InlineData(JobPhase.Completed)]
    [InlineData(JobPhase.PartiallyCompleted)]
    public async Task A_run_that_completed_while_the_app_was_closed_opens_on_Results(JobPhase phase)
    {
        HaveRuns(("job-1", phase));
        Store().Save(new SessionState("RunProgress", "job-1"));

        var target = await new SessionRestorer(Store(), _runs).ResolveAsync(CancellationToken.None);

        target.ShouldBe(new SessionTarget("Runs", "Results", "job-1"));
    }

    [Fact]
    public async Task An_unreadable_history_or_settings_file_means_no_restore_not_a_crash()
    {
        Store().Save(new SessionState("Results", "job-1"));
        _runs.GetAllAsync(Arg.Any<CancellationToken>()).Returns<Task<IReadOnlyList<RunRecord>>>(_ => throw new IOException("db locked"));

        (await new SessionRestorer(Store(), _runs).ResolveAsync(CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task A_remembered_page_name_that_is_not_a_known_page_is_ignored()
    {
        HaveRuns();
        _settings.Values["session.page"] = "../../etc";

        (await new SessionRestorer(Store(), _runs).ResolveAsync(CancellationToken.None)).ShouldBeNull();
    }

    // ---- the shell ----

    private ShellViewModel Shell(SessionRestorer? restorer)
    {
        var strings = Substitute.For<IStringResourceProvider>();
        strings.GetString(Arg.Any<string>()).Returns(call => call.Arg<string>());
        return new ShellViewModel(_inner, Substitute.For<IGcpAccount>(), new WeakReferenceMessenger(), strings, TestMessages.Center(), TestMessages.Completion(), session: restorer);
    }

    [Fact]
    public async Task With_nothing_remembered_the_shell_starts_on_New_run()
    {
        HaveRuns();
        var shell = Shell(new SessionRestorer(Store(), _runs));

        await shell.PrepareSessionAsync(CancellationToken.None);

        shell.StartupNavKey.ShouldBe("NewRun");
    }

    [Fact]
    public async Task The_shell_selects_the_parent_page_then_opens_the_remembered_run_once()
    {
        HaveRuns(("job-1", JobPhase.Completed));
        Store().Save(new SessionState("Results", "job-1"));
        var shell = Shell(new SessionRestorer(Store(), _runs));
        await shell.PrepareSessionAsync(CancellationToken.None);
        shell.StartupNavKey.ShouldBe("Runs");

        shell.NavigateToCommand.Execute("Runs");
        shell.NavigateToCommand.Execute("Runs");

        Received.InOrder(() =>
        {
            _inner.NavigateTo("Runs");
            _inner.NavigateTo("Results", "job-1");
            _inner.NavigateTo("Runs");
        });
        _inner.Received(1).NavigateTo("Results", "job-1");
    }

    [Fact]
    public async Task Clicking_the_nav_item_already_shown_during_startup_still_counts_as_the_users_choice()
    {
        // Cold review of #499: the "already showing, do nothing" shortcut in NavigateTo must not skip the user-choice bookkeeping.
        HaveRuns(("job-1", JobPhase.Completed));
        Store().Save(new SessionState("Results", "job-1"));
        var shell = Shell(new SessionRestorer(Store(), _runs));
        await shell.PrepareSessionAsync(CancellationToken.None);
        _inner.CurrentPageKey.Returns("NewRun");

        shell.NavigateToCommand.Execute("NewRun");
        shell.NavigateToCommand.Execute("Runs");

        _inner.DidNotReceive().NavigateTo("Results", "job-1");
    }

    [Fact]
    public async Task A_click_on_the_shown_item_before_the_session_is_read_leaves_the_selection_alone()
    {
        HaveRuns(("job-1", JobPhase.Completed));
        Store().Save(new SessionState("Results", "job-1"));
        var shell = Shell(new SessionRestorer(Store(), _runs));
        _inner.CurrentPageKey.Returns("Settings");

        shell.NavigateToCommand.Execute("Settings");
        await shell.PrepareSessionAsync(CancellationToken.None);

        shell.StartupNavKey.ShouldBeNull();
    }

    [Fact]
    public async Task Choosing_a_different_page_first_drops_the_pending_restore()
    {
        HaveRuns(("job-1", JobPhase.Completed));
        Store().Save(new SessionState("Results", "job-1"));
        var shell = Shell(new SessionRestorer(Store(), _runs));
        await shell.PrepareSessionAsync(CancellationToken.None);

        shell.NavigateToCommand.Execute("NewRun");
        shell.NavigateToCommand.Execute("Runs");

        _inner.DidNotReceive().NavigateTo("Results", "job-1");
    }

    private ShellViewModel QuickShell(SessionRestorer restorer)
    {
        var shell = Shell(restorer);
        shell.SessionRestoreTimeout = TimeSpan.FromMilliseconds(100);
        return shell;
    }

    [Fact]
    public async Task A_hung_history_database_falls_back_to_New_run_instead_of_blocking_startup()
    {
        var never = new TaskCompletionSource<IReadOnlyList<RunRecord>>();
        _runs.GetAllAsync(Arg.Any<CancellationToken>()).Returns(never.Task);
        Store().Save(new SessionState("Results", "job-1"));
        var shell = QuickShell(new SessionRestorer(Store(), _runs));

        await shell.PrepareSessionAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        shell.StartupNavKey.ShouldBe("NewRun");
        shell.NavigateToCommand.Execute("NewRun");
        _inner.DidNotReceive().NavigateTo("Results", "job-1");
    }

    [Fact]
    public async Task A_settings_file_that_blocks_synchronously_also_falls_back()
    {
        using var release = new ManualResetEventSlim();
        var blocking = new BlockingSettings(release);
        var shell = QuickShell(new SessionRestorer(new SessionStore(blocking), _runs));

        var clock = System.Diagnostics.Stopwatch.StartNew();
        await shell.PrepareSessionAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        release.Set();

        shell.StartupNavKey.ShouldBe("NewRun");
        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
    }

    private sealed class BlockingSettings(ManualResetEventSlim release) : ISettingsStore
    {
        public string? GetString(string key)
        {
            release.Wait(TimeSpan.FromSeconds(8));
            return null;
        }

        public void SetString(string key, string value)
        {
        }
    }

    [Fact]
    public async Task A_click_before_the_restore_resolves_is_not_overwritten()
    {
        HaveRuns(("job-1", JobPhase.Completed));
        Store().Save(new SessionState("Results", "job-1"));
        var shell = Shell(new SessionRestorer(Store(), _runs));

        shell.NavigateToCommand.Execute("Cloud");
        await shell.PrepareSessionAsync(CancellationToken.None);

        shell.StartupNavKey.ShouldBeNull();
        shell.NavigateToCommand.Execute("Runs");
        _inner.DidNotReceive().NavigateTo("Results", "job-1");
    }

    [Fact]
    public async Task A_page_with_no_nav_item_opened_during_the_restore_is_not_replaced_by_New_run()
    {
        HaveRuns(("job-1", JobPhase.Completed));
        Store().Save(new SessionState("Results", "job-1"));
        var shell = Shell(new SessionRestorer(Store(), _runs));

        shell.NavigateToCommand.Execute("Wizard");
        await shell.PrepareSessionAsync(CancellationToken.None);

        shell.StartupNavKey.ShouldBeNull("the window must leave the selection alone, not select the first item");
    }

    [Fact]
    public async Task A_click_while_the_restore_is_still_resolving_is_not_overwritten()
    {
        var gate = new TaskCompletionSource<IReadOnlyList<RunRecord>>();
        _runs.GetAllAsync(Arg.Any<CancellationToken>()).Returns(gate.Task);
        Store().Save(new SessionState("Results", "job-1"));
        var shell = Shell(new SessionRestorer(Store(), _runs));

        var preparing = shell.PrepareSessionAsync(CancellationToken.None);
        shell.NavigateToCommand.Execute("Settings");
        gate.SetResult([new RunRecord("job-1", JobPhase.Completed, DateTimeOffset.UtcNow)]);
        await preparing.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        shell.StartupNavKey.ShouldBeNull();
        shell.NavigateToCommand.Execute("Runs");
        _inner.DidNotReceive().NavigateTo("Results", "job-1");
    }

    [Fact]
    public async Task A_shell_without_a_restorer_starts_on_New_run()
    {
        var shell = Shell(null);

        await shell.PrepareSessionAsync(CancellationToken.None);

        shell.StartupNavKey.ShouldBe("NewRun");
    }
}
