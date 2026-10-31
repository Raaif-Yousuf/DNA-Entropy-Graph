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
    public async Task Every_scope_unreadable_throws_so_the_caller_can_degrade()
    {
        var rig = new Rig("project-b");
        rig.Gcp.WithAccount("sub-x", "x@example.org", "project-a").WithAccountSignedOut("sub-x");
        await rig.RowAsync("job-a", JobPhase.Completed, "project-a", "sub-x", Start);
        var scanner = new LeftRunningScanner(rig.Gcp, rig.Settings, rig.Runs, null, rig.Clock);

        await Should.ThrowAsync<AccountAuthException>(() => scanner.ScanAllAsync(null, CancellationToken.None));
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
        await rig.RowAsync("job-a", JobPhase.Completed, "project-a", "sub-a", Start);
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
        await rig.RowAsync("job-a", JobPhase.Completed, "project-a", "sub-a", Start);
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
        await rig.RowAsync("job-a", JobPhase.Completed, "project-a", "sub-a", Start);
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
        await rig.RowAsync("job-a", JobPhase.Completed, "project-a", "sub-a", Start);
        rig.Choice = 1;

        await rig.Guard.ConfirmExitAsync(CancellationToken.None);

        await rig.Dialog.Received(1).ChooseAsync("LeftRunning_UnreadTitle", Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_scope_unreadable_for_a_reason_other_than_sign_in_is_said_without_a_sign_in_button()
    {
        var rig = new Rig("project-b");
        rig.Gcp.WithAccount("sub-a", "a@example.org", "project-a").WithAccountFailure("sub-a", AuthErrorCodes.NetworkUnavailable);
        await rig.RowAsync("job-a", JobPhase.Completed, "project-a", "sub-a", Start);
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
        await rig.RowAsync("job-a", JobPhase.Completed, "project-a", "sub-a", Start);

        (await rig.Guard.ConfirmExitAsync(CancellationToken.None)).ShouldBeTrue();

        await rig.Dialog.DidNotReceive().ChooseAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_unread_scope_is_logged_once_per_state_change_not_on_every_scan()
    {
        var log = new CountingLog();
        var rig = RigWithSignedOutAccountA(log);
        await rig.RowAsync("job-a", JobPhase.Completed, "project-a", "sub-a", Start);
        rig.Choice = 1;

        await rig.Guard.ConfirmExitAsync(CancellationToken.None);
        await rig.Guard.ConfirmExitAsync(CancellationToken.None);
        await rig.Guard.ConfirmExitAsync(CancellationToken.None);

        log.Lines.Count(l => l == "left-running-scan/unread-scopes").ShouldBe(1);
    }
}
