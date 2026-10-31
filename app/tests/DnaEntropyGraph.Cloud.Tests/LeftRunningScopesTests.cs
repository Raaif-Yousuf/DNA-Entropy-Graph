// Issue #830 milestone 2: the nothing-left-running scan and the exit guard look in every scope a recent run of this installation was made in (plus the selected one),
// stop each computer in the scope it was found in, and never list the same project and account twice.
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Presentation.Services;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

public sealed class LeftRunningScopesTests
{
    private const string Zone = "us-central1-a";
    private static readonly DateTimeOffset Start = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private sealed class KeyStrings : IStringResourceProvider
    {
        public string GetString(string key) => key is "LeftRunning_UnreadBodySignIn" or "SignInNeeded_Joiner" ? (key == "SignInNeeded_Joiner" ? " or " : "UNREAD:{0}") : key;
    }

    private sealed class Rig
    {
        public Rig(string selectedProject, IDiagnosticsLog? log = null)
        {
            Clock = new ManualTimeProvider(Start);
            Gcp = new FakeGcp(Clock).WithSelectedProject(selectedProject);
            Settings = new MemorySettingsStore();
            Installation = InstallationId.GetOrCreate(Settings);
            Scanner = new LeftRunningScanner(Gcp, Settings, Runs, null, Clock);
            Cleanup = new InstallationCleanup(Gcp, Gcp, Runs, clock: Clock);
            Dialog.ChooseAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(call => Task.FromResult<int?>(Choice));
            Guard = new LeftRunningGuard(Scanner, Cleanup, Settings, Dialog, Toasts, new KeyStrings(), log ?? NullDiagnosticsLog.Instance, Clock, Gcp);
        }

        public ManualTimeProvider Clock { get; }

        public FakeGcp Gcp { get; }

        public MemorySettingsStore Settings { get; }

        public string Installation { get; }

        public InMemoryRunRepository Runs { get; } = new();

        public IChoiceDialogService Dialog { get; } = Substitute.For<IChoiceDialogService>();

        public IToastService Toasts { get; } = Substitute.For<IToastService>();

        public LeftRunningScanner Scanner { get; }

        public InstallationCleanup Cleanup { get; }

        public LeftRunningGuard Guard { get; }

        public int? Choice { get; set; } = 0;

        public async Task<VmDescriptor> VmAsync(string project, string sub, string job)
        {
            var spec = new VmSpec(project, Installation, job, "evo2_7b", "0.1.0", "stop", "g2-standard-8", TimeSpan.FromMinutes(90), "DELETE");
            return await Gcp.CreateVmAsync(new CloudScope(project, sub), spec, Zone, CancellationToken.None);
        }

        public Task RowAsync(string job, JobPhase phase, string project, string? sub, DateTimeOffset? finished = null)
            => Runs.UpsertAsync(new RunRecord(job, phase, Start, ProjectId: project, AccountSub: sub, InstallationId: Installation, FinishedAt: finished), CancellationToken.None);
    }

    [Fact]
    public async Task The_scan_lists_in_the_project_of_a_recent_finished_run_not_only_the_selected_one()
    {
        var rig = new Rig("project-b");
        await rig.VmAsync("project-a", FakeGcp.DefaultAccountSub, "job-a");
        await rig.RowAsync("job-a", JobPhase.Completed, "project-a", FakeGcp.DefaultAccountSub, Start);

        var report = await rig.Scanner.ScanAllAsync(rig.Gcp.SelectedScope(), CancellationToken.None);

        report.RunningVms.Select(v => v.Scope!.ProjectId).ShouldBe(["project-a"]);
        rig.Gcp.CallsTo("ListByInstallation").Select(c => c.ProjectId).Order().ToList().ShouldBe(["project-a", "project-b"]);
    }

    [Fact]
    public async Task A_row_older_than_the_lifecycle_lookback_does_not_add_a_scope()
    {
        var rig = new Rig("project-b");
        await rig.RowAsync("job-old", JobPhase.Completed, "project-a", FakeGcp.DefaultAccountSub, Start.AddDays(-30));
        rig.Clock.Advance(TimeSpan.FromDays(1));

        await rig.Scanner.ScanAllAsync(rig.Gcp.SelectedScope(), CancellationToken.None);

        rig.Gcp.CallsTo("ListByInstallation").Select(c => c.ProjectId).ShouldBe(["project-b"]);
    }

    [Fact]
    public async Task A_row_with_no_account_and_the_selected_scope_naming_the_same_project_and_account_are_listed_once()
    {
        var rig = new Rig("project-p");
        await rig.RowAsync("job-legacy", JobPhase.Completed, "project-p", null, Start);

        await rig.Scanner.ScanAllAsync(rig.Gcp.SelectedScope(), CancellationToken.None);

        rig.Gcp.CallsTo("ListByInstallation").Count.ShouldBe(1, "a null-account row acts as the current account, which is the selected one");
        rig.Gcp.CallsTo("ListDisksByInstallation").Count.ShouldBe(1);
    }

    [Fact]
    public async Task The_same_project_under_two_different_accounts_is_listed_once_per_account()
    {
        var rig = new Rig("project-p");
        rig.Gcp.WithAccount("sub-x", "x@example.org", "project-p");
        await rig.RowAsync("job-x", JobPhase.Completed, "project-p", "sub-x", Start);

        await rig.Scanner.ScanAllAsync(rig.Gcp.SelectedScope(), CancellationToken.None);

        rig.Gcp.CallsTo("ListByInstallation").Select(c => c.AccountSub).Order().ToList().ShouldBe([FakeGcp.DefaultAccountSub, "sub-x"]);
    }

    [Fact]
    public async Task A_live_run_in_one_project_does_not_make_a_same_named_job_in_another_project_live()
    {
        var rig = new Rig("project-b");
        await rig.VmAsync("project-b", FakeGcp.DefaultAccountSub, "job-1");
        await rig.RowAsync("job-1", JobPhase.Running, "project-a", FakeGcp.DefaultAccountSub);

        var report = await rig.Scanner.ScanAllAsync(rig.Gcp.SelectedScope(), CancellationToken.None);

        report.RunningVms.Single().HasLiveRun.ShouldBeFalse("the live row belongs to project-a; the VM is in project-b");
    }

    [Fact]
    public async Task One_scope_that_cannot_be_read_does_not_hide_the_others_and_is_counted()
    {
        var rig = new Rig("project-b");
        rig.Gcp.WithAccount("sub-x", "x@example.org", "project-a").WithAccountSignedOut("sub-x");
        await rig.VmAsync("project-b", FakeGcp.DefaultAccountSub, "job-b");
        await rig.RowAsync("job-a", JobPhase.Completed, "project-a", "sub-x", Start);

        var report = await rig.Scanner.ScanAllAsync(rig.Gcp.SelectedScope(), CancellationToken.None);

        report.RunningVms.Select(v => v.Scope!.ProjectId).ShouldBe(["project-b"]);
        report.UnreadScopes.ShouldBe(1);
    }

    [Fact]
    public async Task A_project_two_accounts_can_both_reach_lists_each_computer_once()
    {
        // Issue #830 review (d): the scan lists once per (project, account), so a shared project is listed twice; the merged report must not name one computer twice.
        var rig = new Rig("project-x");
        rig.Gcp.WithAccount("sub-a", "a@example.org", "project-x");
        await rig.VmAsync("project-x", FakeGcp.DefaultAccountSub, "job-x");
        await rig.RowAsync("job-x", JobPhase.Completed, "project-x", "sub-a", Start);

        var report = await rig.Scanner.ScanAllAsync(rig.Gcp.SelectedScope(), CancellationToken.None);

        report.RunningVms.Count.ShouldBe(1);
        report.RunningVms.Select(v => v.Name).Distinct().Count().ShouldBe(1);
    }

    [Fact]
    public async Task A_computer_both_accounts_can_see_is_kept_under_the_account_of_the_run_it_belongs_to()
    {
        // Issue #830 review F7c: job-1's account is listed first, but the computer belongs to job-2, made by sub-b.
        var rig = new Rig("project-p");
        rig.Gcp.WithAccount("sub-a", "a@example.org", "project-p").WithAccount("sub-b", "b@example.org", "project-p");
        await rig.VmAsync("project-p", "sub-b", "job-2");
        await rig.RowAsync("job-1", JobPhase.Completed, "project-p", "sub-a", Start);
        await rig.RowAsync("job-2", JobPhase.Running, "project-p", "sub-b");

        var report = await rig.Scanner.ScanAllAsync(rig.Gcp.SelectedScope(), CancellationToken.None);

        report.RunningVms.Single().Scope!.AccountSub.ShouldBe("sub-b");
    }

    [Fact]
    public async Task Every_scope_unreadable_returns_a_report_listing_each_instead_of_throwing_the_first_failure()
    {
        // Review F2: with every scope failing the first failure used to be thrown, so the exit guard saw a generic error and said nothing.
        var rig = new Rig("project-b");
        rig.Gcp.WithAccount("sub-x", "x@example.org", "project-a").WithAccountSignedOut("sub-x");
        rig.Gcp.WithAccount("sub-y", "y@example.org", "project-c").WithAccountFailure("sub-y", AuthErrorCodes.NetworkUnavailable);
        await rig.RowAsync("job-a", JobPhase.Running, "project-a", "sub-x");
        await rig.RowAsync("job-c", JobPhase.Running, "project-c", "sub-y");
        var scanner = new LeftRunningScanner(rig.Gcp, rig.Settings, rig.Runs, null, rig.Clock);

        var report = await scanner.ScanAllAsync(null, CancellationToken.None);

        report.RunningVms.ShouldBeEmpty();
        report.UnreadScopes.ShouldBe(2);
        report.UnreadList.Select(u => (u.Scope.AccountSub, u.NeedsSignIn, u.HasObligation)).ShouldBe(
            [("sub-x", true, true), ("sub-y", false, true)], ignoreOrder: true);
    }

    [Fact]
    public async Task Stop_at_exit_stops_a_computer_in_the_project_and_account_it_was_found_in_while_another_project_is_selected()
    {
        var rig = new Rig("project-b");
        rig.Gcp.WithAccount("sub-a", "a@example.org", "project-a");
        await rig.VmAsync("project-a", "sub-a", "job-a");
        await rig.RowAsync("job-a", JobPhase.Completed, "project-a", "sub-a", Start);
        rig.Choice = 0;

        (await rig.Guard.ConfirmExitAsync(CancellationToken.None)).ShouldBeTrue();

        var stops = rig.Gcp.Calls.Where(c => c.Method is "StopVm" or "DeleteVm").ToList();
        stops.ShouldNotBeEmpty("the computer in project-a must have been stopped");
        stops.ShouldAllBe(c => c.ProjectId == "project-a" && c.AccountSub == "sub-a");
    }

    [Fact]
    public async Task Delete_at_exit_deletes_in_the_scope_the_computer_was_found_in()
    {
        var rig = new Rig("project-b");
        rig.Gcp.WithAccount("sub-a", "a@example.org", "project-a");
        await rig.VmAsync("project-a", "sub-a", "job-a");
        await rig.RowAsync("job-a", JobPhase.Completed, "project-a", "sub-a", Start);
        rig.Choice = 1;

        (await rig.Guard.ConfirmExitAsync(CancellationToken.None)).ShouldBeTrue();

        var deletes = rig.Gcp.CallsTo("DeleteVm");
        deletes.ShouldNotBeEmpty();
        deletes.ShouldAllBe(c => c.ProjectId == "project-a" && c.AccountSub == "sub-a");
        (await rig.Gcp.ListByInstallationAsync(new CloudScope("project-a", "sub-a"), rig.Installation, CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task The_legacy_cleanup_deletes_each_computer_in_its_own_scope()
    {
        var rig = new Rig("project-b");
        rig.Gcp.WithAccount("sub-a", "a@example.org", "project-a");
        var vm = await rig.VmAsync("project-a", "sub-a", "job-a");

        var result = await rig.Cleanup.EndVmAsync(new CloudScope("project-a", "sub-a"), rig.Installation, vm, true, null, CancellationToken.None);

        result.ShouldBe(VmEndResult.Done);
        rig.Gcp.CallsTo("DeleteVm").ShouldAllBe(c => c.ProjectId == "project-a" && c.AccountSub == "sub-a");
    }

    [Fact]
    public void Dedupe_treats_a_null_account_as_the_selected_one_and_keeps_distinct_accounts_apart()
    {
        var selected = new CloudScope("p", "s1");

        var scopes = CloudScopes.Dedupe([new CloudScope("p", null), new CloudScope("p", "s1"), new CloudScope("p", "s2"), new CloudScope("q", null)], selected);

        scopes.ShouldBe([new CloudScope("p", "s1"), new CloudScope("p", "s2"), new CloudScope("q", "s1")], ignoreOrder: true);
    }

    [Fact]
    public void Dedupe_with_no_selection_leaves_a_null_account_alone()
    {
        CloudScopes.Dedupe([new CloudScope("p", null), new CloudScope("p", null)], null).ShouldBe([new CloudScope("p", null)]);
    }

    // ---- Issue #830 review M4: an unreadable scope is said at exit, not only logged ----

    private sealed class CountingLog : IDiagnosticsLog
    {
        public List<string> Lines { get; } = [];

        public void Warning(string source, string? jobId, string errorClass) => Lines.Add($"{source}/{errorClass}");
    }

    private static Rig RigWithSignedOutAccountA(IDiagnosticsLog? log = null)
    {
        var rig = new Rig("project-b", log);
        rig.Gcp.WithAccount("sub-a", "a@example.org", "project-a").WithAccountSignedOut("sub-a");
        return rig;
    }

    [Fact]
    public async Task Exit_with_one_account_signed_out_says_so_and_offers_sign_in_even_when_nothing_else_is_running()
    {
        var rig = RigWithSignedOutAccountA();
        await rig.RowAsync("job-a", JobPhase.Running, "project-a", "sub-a");
        string? title = null;
        string? body = null;
        IReadOnlyList<string>? buttons = null;
        rig.Dialog.ChooseAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            (title, body, buttons) = (call.ArgAt<string>(0), call.ArgAt<string>(1), call.ArgAt<IReadOnlyList<string>>(2));
            return Task.FromResult<int?>(1);
        });

        var mayClose = await rig.Guard.ConfirmExitAsync(CancellationToken.None);

        title.ShouldBe("LeftRunning_UnreadTitle");
        body.ShouldBe("UNREAD:a@example.org", "the account whose computers could not be checked is named");
        buttons.ShouldBe(["LeftRunning_UnreadSignIn", "LeftRunning_UnreadCloseAnyway"]);
        mayClose.ShouldBeTrue("Close anyway on a scope that cannot be read never traps the person");
    }

    [Fact]
    public async Task Choosing_Sign_in_at_that_prompt_starts_sign_in_and_keeps_the_window_open()
    {
        var rig = RigWithSignedOutAccountA();
        await rig.RowAsync("job-a", JobPhase.Running, "project-a", "sub-a");
        rig.Choice = 0;

        var mayClose = await rig.Guard.ConfirmExitAsync(CancellationToken.None);

        rig.Gcp.SignInAttempts.ShouldBe(1);
        mayClose.ShouldBeFalse("after signing in the next close scans again");
    }

    [Fact]
    public async Task Close_anyway_on_the_unread_prompt_goes_on_to_the_running_computers_prompt()
    {
        var rig = RigWithSignedOutAccountA();
        await rig.VmAsync("project-b", FakeGcp.DefaultAccountSub, "job-b");
        await rig.RowAsync("job-a", JobPhase.Running, "project-a", "sub-a");
        var titles = new List<string>();
        rig.Dialog.ChooseAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            titles.Add(call.ArgAt<string>(0));
            return Task.FromResult<int?>(titles.Count == 1 ? 1 : 2);
        });

        (await rig.Guard.ConfirmExitAsync(CancellationToken.None)).ShouldBeTrue();

        titles.ShouldBe(["LeftRunning_UnreadTitle", "LeftRunning_ExitTitle"]);
    }

    [Fact]
    public async Task Every_account_signed_out_still_says_so_at_exit_instead_of_closing_silently()
    {
        var rig = RigWithSignedOutAccountA();
        rig.Gcp.WithSignedOut();
        await rig.RowAsync("job-a", JobPhase.Running, "project-a", "sub-a");
        rig.Choice = 1;

        await rig.Guard.ConfirmExitAsync(CancellationToken.None);

        await rig.Dialog.Received(1).ChooseAsync("LeftRunning_UnreadTitle", Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_scope_unreadable_for_a_reason_other_than_sign_in_is_said_without_a_sign_in_button()
    {
        var rig = new Rig("project-b");
        rig.Gcp.WithAccount("sub-a", "a@example.org", "project-a").WithAccountFailure("sub-a", AuthErrorCodes.NetworkUnavailable);
        await rig.RowAsync("job-a", JobPhase.Running, "project-a", "sub-a");
        IReadOnlyList<string>? buttons = null;
        rig.Dialog.ChooseAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            buttons = call.ArgAt<IReadOnlyList<string>>(2);
            return Task.FromResult<int?>(0);
        });

        (await rig.Guard.ConfirmExitAsync(CancellationToken.None)).ShouldBeTrue();

        buttons.ShouldBe(["LeftRunning_UnreadCloseAnyway"]);
    }

    [Fact]
    public async Task A_fully_readable_exit_shows_no_unread_prompt()
    {
        var rig = new Rig("project-b");
        rig.Gcp.WithAccount("sub-a", "a@example.org", "project-a");
        await rig.RowAsync("job-a", JobPhase.Running, "project-a", "sub-a");

        (await rig.Guard.ConfirmExitAsync(CancellationToken.None)).ShouldBeTrue();

        await rig.Dialog.DidNotReceive().ChooseAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_unread_scope_is_logged_once_per_state_change_not_on_every_scan()
    {
        var log = new CountingLog();
        var rig = RigWithSignedOutAccountA(log);
        await rig.RowAsync("job-a", JobPhase.Running, "project-a", "sub-a");
        rig.Choice = 1;

        await rig.Guard.ConfirmExitAsync(CancellationToken.None);
        await rig.Guard.ConfirmExitAsync(CancellationToken.None);
        await rig.Guard.ConfirmExitAsync(CancellationToken.None);

        log.Lines.Count(l => l == "left-running-scan/unread-scopes").ShouldBe(1);
    }

    // ---- Issue #830 review F2 and F4 ----

    [Fact]
    public async Task One_account_offline_and_another_signed_out_still_prompts_to_sign_in_naming_the_signed_out_one()
    {
        var rig = new Rig("project-b");
        rig.Gcp.WithAccount("sub-a", "a@example.org", "project-a").WithAccountSignedOut("sub-a");
        rig.Gcp.WithAccount("sub-c", "c@example.org", "project-c").WithAccountFailure("sub-c", AuthErrorCodes.NetworkUnavailable);
        await rig.RowAsync("job-a", JobPhase.Running, "project-a", "sub-a");
        await rig.RowAsync("job-c", JobPhase.Running, "project-c", "sub-c");
        rig.Gcp.WithSignedOut();
        string? body = null;
        IReadOnlyList<string>? buttons = null;
        rig.Dialog.ChooseAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            (body, buttons) = (call.ArgAt<string>(1), call.ArgAt<IReadOnlyList<string>>(2));
            return Task.FromResult<int?>(1);
        });

        (await rig.Guard.ConfirmExitAsync(CancellationToken.None)).ShouldBeTrue();

        body.ShouldBe("UNREAD:a@example.org");
        buttons.ShouldBe(["LeftRunning_UnreadSignIn", "LeftRunning_UnreadCloseAnyway"]);
    }

    [Fact]
    public async Task Two_signed_out_accounts_are_both_named()
    {
        var rig = new Rig("project-b");
        rig.Gcp.WithAccount("sub-a", "a@example.org", "project-a").WithAccountSignedOut("sub-a");
        rig.Gcp.WithAccount("sub-d", "d@example.org", "project-d").WithAccountSignedOut("sub-d");
        await rig.RowAsync("job-a", JobPhase.Running, "project-a", "sub-a");
        await rig.RowAsync("job-d", JobPhase.Running, "project-d", "sub-d");
        rig.Gcp.WithSignedOut();
        string? body = null;
        rig.Dialog.ChooseAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            body = call.ArgAt<string>(1);
            return Task.FromResult<int?>(1);
        });

        await rig.Guard.ConfirmExitAsync(CancellationToken.None);

        body.ShouldBe("UNREAD:a@example.org or d@example.org");
    }

    [Fact]
    public async Task Every_scope_offline_says_so_with_Close_anyway_only_when_a_run_could_still_be_running()
    {
        var rig = new Rig("project-b");
        rig.Gcp.WithAccount("sub-c", "c@example.org", "project-c").WithAccountFailure("sub-c", AuthErrorCodes.NetworkUnavailable);
        await rig.RowAsync("job-c", JobPhase.Running, "project-c", "sub-c");
        rig.Gcp.WithSignedOut();
        IReadOnlyList<string>? buttons = null;
        rig.Dialog.ChooseAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            buttons = call.ArgAt<IReadOnlyList<string>>(2);
            return Task.FromResult<int?>(0);
        });

        (await rig.Guard.ConfirmExitAsync(CancellationToken.None)).ShouldBeTrue();

        buttons.ShouldBe(["LeftRunning_UnreadCloseAnyway"]);
    }

    [Fact]
    public async Task A_signed_out_account_whose_only_run_ended_with_its_computer_confirmed_gone_is_not_prompted_for()
    {
        var rig = RigWithSignedOutAccountA();
        await rig.RowAsync("job-a", JobPhase.Completed, "project-a", "sub-a", Start);

        (await rig.Guard.ConfirmExitAsync(CancellationToken.None)).ShouldBeTrue();

        await rig.Dialog.DidNotReceive().ChooseAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(10)]
    [InlineData(31)]
    [InlineData(60 * 24)]
    public async Task A_keep_alive_row_obliges_a_prompt_before_AND_after_its_window_expires_because_a_signed_out_account_cannot_end_the_computer(int minutesLater)
    {
        // Review M1 (round 3): the computer is held until something stops it; with the account signed out nothing can, so the expiry is no end.
        var rig = RigWithSignedOutAccountA();
        var options = new DnaEntropyGraph.Core.RunOptions { ModelId = "evo2_7b", RunTarget = "Cloud", AfterTask = DnaEntropyGraph.Core.AfterTaskAction.KeepAlive, KeepAliveMinutes = 30 };
        await rig.Runs.UpsertAsync(new RunRecord("job-k", JobPhase.Completed, Start, ProjectId: "project-a", AccountSub: "sub-a", InstallationId: rig.Installation, FinishedAt: Start,
            OptionsJson: DnaEntropyGraph.Core.Runs.RunOptionsJson.Serialize(options)), CancellationToken.None);
        rig.Clock.Advance(TimeSpan.FromMinutes(minutesLater));

        await rig.Guard.ConfirmExitAsync(CancellationToken.None);

        await rig.Dialog.Received(1).ChooseAsync("LeftRunning_UnreadTitle", Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void An_expired_keep_alive_row_keeps_its_obligation_until_it_leaves_the_lifecycle_lookback_and_then_drops_it()
    {
        var options = new DnaEntropyGraph.Core.RunOptions { ModelId = "evo2_7b", RunTarget = "Cloud", AfterTask = DnaEntropyGraph.Core.AfterTaskAction.KeepAlive, KeepAliveMinutes = 30 };
        var row = new RunRecord("job-k", JobPhase.Completed, Start, ProjectId: "project-a", AccountSub: "sub-a", FinishedAt: Start, OptionsJson: DnaEntropyGraph.Core.Runs.RunOptionsJson.Serialize(options));

        RunObligations.HasObligation(row, Start.AddMinutes(31)).ShouldBeTrue();
        RunObligations.HasObligation(row, Start.AddDays(13)).ShouldBeTrue();
        RunObligations.HasObligation(row, Start.AddDays(15)).ShouldBeFalse("past the lookback no sweep looks at the row any more");
    }

    // ---- Review M2 (round 3): one matching rule for a row with no account, on both the exit-guard path and the reconciler path ----

    [Fact]
    public void A_row_with_no_account_obliges_the_account_that_was_current_on_both_matching_paths()
    {
        var row = new RunRecord("job-n", JobPhase.Running, Start, ProjectId: "project-a", AccountSub: null, InstallationId: "inst");
        IReadOnlyList<RunRecord> rows = [row];

        RunObligations.Exist(rows, new CloudScope("project-a", "sub-a"), "inst", Start).ShouldBeTrue("exit-guard path");
        RunObligations.ExistForAccount(rows, "sub-a", "inst", Start).ShouldBeTrue("reconciler path");
        RunObligations.ExistForAccount(rows, "sub-b", "inst", Start).ShouldBeTrue("with no recorded account the row acts as whichever account is current, so any named account may owe it");
    }

    [Fact]
    public void A_row_that_names_another_account_does_not_oblige_this_one_on_either_path()
    {
        var row = new RunRecord("job-n", JobPhase.Running, Start, ProjectId: "project-a", AccountSub: "sub-b", InstallationId: "inst");
        IReadOnlyList<RunRecord> rows = [row];

        RunObligations.Exist(rows, new CloudScope("project-a", "sub-a"), "inst", Start).ShouldBeFalse();
        RunObligations.ExistForAccount(rows, "sub-a", "inst", Start).ShouldBeFalse();
    }

    [Fact]
    public async Task The_exit_guard_prompts_for_a_signed_out_account_whose_only_unfinished_run_row_has_no_account()
    {
        // Two accounts are modelled, so no backfill ever gives the legacy row an account; the current one (sub-a) is the selected scope and cannot be read.
        var rig = new Rig("project-b");
        rig.Gcp.WithAccount("sub-a", "a@example.org", "project-a").WithAccountProject("sub-a", "project-a").WithAccountFailure("sub-a", AuthErrorCodes.NetworkUnavailable);
        await rig.Gcp.SwitchAccountAsync("sub-a", CancellationToken.None);
        await rig.RowAsync("job-legacy", JobPhase.Running, "project-a", null);

        await rig.Guard.ConfirmExitAsync(CancellationToken.None);

        await rig.Dialog.Received(1).ChooseAsync("LeftRunning_UnreadTitle", Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    // ---- Review L3 (round 3): the selected scope is always worth a prompt when it cannot be read ----

    [Fact]
    public async Task An_unreadable_SELECTED_scope_with_no_run_rows_is_prompted_for_when_the_cause_is_an_expired_sign_in()
    {
        // Round 4 M-A(1): the active account is signed out, so the selection reads null; its own snapshot (account and project, even when it must sign in) is still the scope this exit exists to look in.
        var rig = new Rig("project-b");
        rig.Gcp.WithAccount("sub-a", "a@example.org", "project-a").WithAccountProject("sub-a", "project-a").WithAccountSignedOut("sub-a");
        await rig.Gcp.SwitchAccountAsync("sub-a", CancellationToken.None);
        rig.Gcp.SelectedScope().ShouldBeNull("the active account is signed out, so the plain selection is empty");
        IReadOnlyList<string>? buttons = null;
        rig.Dialog.ChooseAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            buttons = call.ArgAt<IReadOnlyList<string>>(2);
            return Task.FromResult<int?>(1);
        });

        (await rig.Guard.ConfirmExitAsync(CancellationToken.None)).ShouldBeTrue();

        buttons.ShouldBe(["LeftRunning_UnreadSignIn", "LeftRunning_UnreadCloseAnyway"]);
    }

    [Fact]
    public async Task An_unreadable_SELECTED_scope_with_no_run_rows_that_is_only_offline_is_logged_and_never_prompted()
    {
        // Round 4 M-A(2): offline with nothing recorded is not worth a dialog at every exit; the log line is the trace.
        var log = new CountingLog();
        var rig = new Rig("project-b", log);
        rig.Gcp.WithAccount("sub-a", "a@example.org", "project-a").WithAccountProject("sub-a", "project-a").WithAccountFailure("sub-a", AuthErrorCodes.NetworkUnavailable);
        await rig.Gcp.SwitchAccountAsync("sub-a", CancellationToken.None);

        (await rig.Guard.ConfirmExitAsync(CancellationToken.None)).ShouldBeTrue();

        await rig.Dialog.DidNotReceive().ChooseAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        log.Lines.ShouldContain("left-running-scan/unread-scopes");
    }

    [Fact]
    public async Task An_unreadable_SELECTED_scope_that_is_only_offline_but_has_an_unfinished_run_is_still_prompted_for()
    {
        var rig = new Rig("project-b");
        rig.Gcp.WithAccount("sub-a", "a@example.org", "project-a").WithAccountProject("sub-a", "project-a").WithAccountFailure("sub-a", AuthErrorCodes.NetworkUnavailable);
        await rig.Gcp.SwitchAccountAsync("sub-a", CancellationToken.None);
        await rig.RowAsync("job-a", JobPhase.Running, "project-a", "sub-a");

        await rig.Guard.ConfirmExitAsync(CancellationToken.None);

        await rig.Dialog.Received(1).ChooseAsync("LeftRunning_UnreadTitle", Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    // ---- Round 4 M-B and L-1 ----

    private static RunRecord KeepAliveRow(DateTimeOffset? confirmedEnd) => new("job-k", JobPhase.Completed, Start, ProjectId: "project-a", AccountSub: "sub-a", InstallationId: "inst", FinishedAt: Start,
        OptionsJson: DnaEntropyGraph.Core.Runs.RunOptionsJson.Serialize(new DnaEntropyGraph.Core.RunOptions { ModelId = "evo2_7b", RunTarget = "Cloud", AfterTask = DnaEntropyGraph.Core.AfterTaskAction.KeepAlive, KeepAliveMinutes = 30 }),
        VmEndConfirmedAt: confirmedEnd);

    [Fact]
    public void A_keep_alive_row_whose_computer_was_confirmed_ended_has_no_obligation_and_an_unconfirmed_one_does()
    {
        RunObligations.HasObligation(KeepAliveRow(null), Start.AddHours(2)).ShouldBeTrue();
        RunObligations.HasObligation(KeepAliveRow(Start.AddMinutes(31)), Start.AddHours(2)).ShouldBeFalse();
    }

    [Fact]
    public void A_confirmed_end_older_than_the_runs_finish_belongs_to_an_earlier_attempt_and_does_not_count()
    {
        // The run was resumed and finished again after the earlier confirmation.
        RunObligations.HasObligation(KeepAliveRow(Start.AddMinutes(-5)), Start.AddHours(2)).ShouldBeTrue();
    }

    [Fact]
    public void A_confirmed_end_clears_an_earlier_unconfirmed_end_code_and_never_clears_an_unfinished_run()
    {
        var unconfirmed = new RunRecord("job-u", JobPhase.Failed, Start, ProjectId: "project-a", AccountSub: "sub-a", FinishedAt: Start, ErrorCode: RunErrorCodes.VmEndUnconfirmed);
        RunObligations.HasObligation(unconfirmed, Start.AddMinutes(1)).ShouldBeTrue();
        RunObligations.HasObligation(unconfirmed with { VmEndConfirmedAt = Start.AddMinutes(1) }, Start.AddMinutes(2)).ShouldBeFalse();
        RunObligations.HasObligation(new RunRecord("job-r", JobPhase.Running, Start, VmEndConfirmedAt: Start.AddMinutes(1)), Start.AddMinutes(2)).ShouldBeTrue();
    }

    [Fact]
    public async Task A_signed_out_account_whose_keep_alive_computer_the_reconciler_already_confirmed_ended_is_not_prompted_for_but_an_unconfirmed_one_is()
    {
        var confirmed = RigWithSignedOutAccountA();
        await confirmed.Runs.UpsertAsync(KeepAliveRow(Start.AddMinutes(31)) with { InstallationId = confirmed.Installation }, CancellationToken.None);
        confirmed.Clock.Advance(TimeSpan.FromHours(2));
        await confirmed.Guard.ConfirmExitAsync(CancellationToken.None);
        await confirmed.Dialog.DidNotReceive().ChooseAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());

        var unconfirmed = RigWithSignedOutAccountA();
        await unconfirmed.Runs.UpsertAsync(KeepAliveRow(null) with { InstallationId = unconfirmed.Installation }, CancellationToken.None);
        unconfirmed.Clock.Advance(TimeSpan.FromHours(2));
        await unconfirmed.Guard.ConfirmExitAsync(CancellationToken.None);
        await unconfirmed.Dialog.Received(1).ChooseAsync("LeftRunning_UnreadTitle", Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void An_outcome_with_no_account_matches_only_rows_with_no_recorded_account()
    {
        var named = new RunRecord("job-n", JobPhase.Running, Start, ProjectId: "project-a", AccountSub: "sub-b", InstallationId: "inst");
        var legacy = new RunRecord("job-l", JobPhase.Running, Start, ProjectId: "project-a", AccountSub: null, InstallationId: "inst");

        RunObligations.ExistForAccount([named], null, "inst", Start).ShouldBeFalse("a row that names another account is not owed by an outcome that names none");
        RunObligations.ExistForAccount([legacy], null, "inst", Start).ShouldBeTrue();
        RunObligations.ExistForAccount([named, legacy], null, "inst", Start).ShouldBeTrue();
    }

    [Fact]
    public async Task An_unreadable_scope_that_is_not_selected_and_whose_only_row_holds_nothing_that_could_run_is_still_only_logged()
    {
        // The selected scope is readable (project-b); the unreadable one has only a Completed row with its computer confirmed gone: nothing to protect.
        var rig = new Rig("project-b");
        rig.Gcp.WithAccount("sub-x", "x@example.org", "project-a").WithAccountFailure("sub-x", AuthErrorCodes.NetworkUnavailable);
        await rig.RowAsync("job-old", JobPhase.Completed, "project-a", "sub-x", Start);

        await rig.Guard.ConfirmExitAsync(CancellationToken.None);

        await rig.Dialog.DidNotReceive().ChooseAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_finished_run_whose_computer_end_was_not_confirmed_obliges_a_prompt()
    {
        var rig = RigWithSignedOutAccountA();
        await rig.Runs.UpsertAsync(new RunRecord("job-u", JobPhase.Failed, Start, ProjectId: "project-a", AccountSub: "sub-a", InstallationId: rig.Installation, FinishedAt: Start,
            ErrorCode: RunErrorCodes.VmEndUnconfirmed), CancellationToken.None);

        await rig.Guard.ConfirmExitAsync(CancellationToken.None);

        await rig.Dialog.Received(1).ChooseAsync("LeftRunning_UnreadTitle", Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }
}
