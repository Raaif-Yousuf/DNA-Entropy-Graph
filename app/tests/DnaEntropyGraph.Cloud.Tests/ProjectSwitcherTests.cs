using System.Data.Common;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>
/// Issue #260: a project switch is a change of the account's project, never a new sign-in, and (since #830 gave every call on a run its own project) never refused
/// because of a run: a run in project A keeps going and ending in A whatever is selected afterwards.
/// </summary>
public class ProjectSwitcherTests
{
    private const string Zone = "us-central1-a";
    private static readonly DateTimeOffset Launch = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    private sealed class MutableClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private readonly MutableClock _clock = new(Launch);
    private readonly MemoryProjectStore _store = new();
    private readonly InMemoryRunRepository _runs = new();

    private FakeGcp NewGcp(string project = "project-a") => new FakeGcp(_clock).WithSelectedProject(project);

    private ProjectSwitcher Switcher(FakeGcp gcp, IProjectSetupGateway? setup = null) => new(gcp, _store, _runs, setup ?? gcp);

    private static IProjectSetupGateway SetupThrowing(Exception failure)
    {
        var setup = Substitute.For<IProjectSetupGateway>();
        setup.GetProjectStateAsync(Arg.Any<CloudScope>(), Arg.Any<CancellationToken>()).Returns<Task<ProjectLifecycleState>>(_ => throw failure);
        return setup;
    }

    private static CloudOperationException Cloud(CloudErrorKind kind, string? code = null) => new(new CloudError(code, null, "x"), kind);

    [Fact]
    public async Task Switching_changes_the_selected_project_and_restores_its_remembered_bucket_without_a_sign_in()
    {
        var gcp = NewGcp();
        _store.Rows["project-b"] = new ProjectSettings("project-b", Bucket: "bucket-b");

        var restored = await Switcher(gcp).SwitchAsync("project-b", CancellationToken.None);

        gcp.SelectedProjectId.ShouldBe("project-b");
        gcp.SelectedScope().ShouldBe(new CloudScope("project-b", FakeGcp.DefaultAccountSub));
        gcp.IsSignedIn.ShouldBeTrue();
        restored.Bucket.ShouldBe("bucket-b");
        _store.Chosen[FakeGcp.DefaultAccountSub].ShouldContain("project-b", "the project is recorded under the account that chose it");
    }

    [Fact]
    public async Task An_illegal_project_id_changes_nothing_and_remembers_nothing()
    {
        var gcp = NewGcp();

        var failure = await Should.ThrowAsync<AccountAuthException>(() => Switcher(gcp).SwitchAsync("NOT LEGAL", CancellationToken.None));

        failure.Code.ShouldBe(AuthErrorCodes.ProjectInvalid);
        gcp.SelectedProjectId.ShouldBe("project-a");
        _store.Rows.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_project_Google_cannot_open_is_refused_before_anything_changes()
    {
        var gcp = NewGcp().WithProjectState("project-b", ProjectLifecycleState.NotFound);

        var failure = await Should.ThrowAsync<AccountAuthException>(() => Switcher(gcp).SwitchAsync("project-b", CancellationToken.None));

        failure.Code.ShouldBe(AuthErrorCodes.ProjectUnreachable);
        AuthErrorCodes.ActionResourceKey(failure.Code).ShouldBe("AuthAction_ChooseAnotherProject");
        gcp.SelectedProjectId.ShouldBe("project-a");
        _store.Rows.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_project_this_account_may_not_use_is_unreachable_and_asked_as_this_account_in_that_project()
    {
        var gcp = NewGcp().WithAccount("sub-1", "a@example.test", "project-a").WithAccountProject("sub-1", "project-a");
        await gcp.SwitchAccountAsync("sub-1", CancellationToken.None);

        var failure = await Should.ThrowAsync<AccountAuthException>(() => Switcher(gcp).SwitchAsync("project-b", CancellationToken.None));

        failure.Code.ShouldBe(AuthErrorCodes.ProjectUnreachable);
        gcp.CallsTo("GetProjectState").ShouldBe([new FakeGcp.FakeCall("GetProjectState", "project-b", "sub-1")], "the check is made in the TARGET project as the current account");
        gcp.SelectedProjectId.ShouldBe("project-a");
    }

    [Fact]
    public async Task A_switch_with_no_connection_names_the_network_not_the_project()
    {
        var gcp = NewGcp();

        var failure = await Should.ThrowAsync<AccountAuthException>(() => Switcher(gcp, SetupThrowing(Cloud(CloudErrorKind.Network))).SwitchAsync("project-b", CancellationToken.None));

        failure.Code.ShouldBe(AuthErrorCodes.NetworkUnavailable);
        AuthErrorCodes.ActionResourceKey(failure.Code).ShouldBe("AuthAction_TryAgain");
        gcp.SelectedProjectId.ShouldBe("project-a");
        _store.Rows.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_build_with_no_Google_connection_is_the_network_answer_not_PROJECT_UNREACHABLE()
    {
        // Review: CLOUD_NOT_CONNECTED is a "no answer" error of kind Other; the reconciler already treats it like a network failure. The project is not at fault.
        var gcp = NewGcp().WithCloudNotConnected();

        var failure = await Should.ThrowAsync<AccountAuthException>(() => Switcher(gcp).SwitchAsync("project-b", CancellationToken.None));

        failure.Code.ShouldBe(AuthErrorCodes.NetworkUnavailable);
        gcp.SelectedProjectId.ShouldBe("project-a");
    }

    [Fact]
    public async Task A_sign_in_problem_from_the_check_keeps_its_own_code_and_is_never_called_a_project_problem()
    {
        var gcp = NewGcp();

        var failure = await Should.ThrowAsync<AccountAuthException>(
            () => Switcher(gcp, SetupThrowing(new AccountAuthException(AuthErrorCodes.SigninExpired))).SwitchAsync("project-b", CancellationToken.None));

        failure.Code.ShouldBe(AuthErrorCodes.SigninExpired);
        gcp.SelectedProjectId.ShouldBe("project-a");
    }

    [Fact]
    public async Task Any_other_failure_of_the_check_becomes_the_named_project_message_and_cancellation_is_not_swallowed()
    {
        var gcp = NewGcp();

        var failure = await Should.ThrowAsync<AccountAuthException>(
            () => Switcher(gcp, SetupThrowing(new InvalidOperationException("boom"))).SwitchAsync("project-b", CancellationToken.None));
        failure.Code.ShouldBe(AuthErrorCodes.ProjectUnreachable);
        failure.InnerException.ShouldBeOfType<InvalidOperationException>();

        await Should.ThrowAsync<OperationCanceledException>(
            () => Switcher(gcp, SetupThrowing(new OperationCanceledException())).SwitchAsync("project-b", CancellationToken.None));
        gcp.SelectedProjectId.ShouldBe("project-a");
    }

    public static TheoryData<Exception> StoreFailures => new()
    {
        new IOException("disk"),
        new InvalidOperationException("db closed"),
        new TestDbException(),
        new UnauthorizedAccessException("read-only folder"),
    };

    private sealed class TestDbException : DbException
    {
        public TestDbException()
            : base("locked")
        {
        }
    }

    [Theory]
    [MemberData(nameof(StoreFailures))]
    public async Task A_store_failure_is_ProjectSaveFailed_and_the_account_keeps_its_project(Exception failureOfStore)
    {
        var gcp = NewGcp();
        _store.FailWith = failureOfStore;

        var failure = await Should.ThrowAsync<AccountAuthException>(() => Switcher(gcp).SwitchAsync("project-b", CancellationToken.None));

        failure.Code.ShouldBe(AuthErrorCodes.ProjectSaveFailed);
        gcp.SelectedProjectId.ShouldBe("project-a", "the account is selected last, so a store failure leaves the card and the account agreeing");
    }

    [Fact]
    public async Task Choosing_the_project_already_in_use_asks_Google_nothing_and_changes_nothing_else()
    {
        var gcp = NewGcp();

        await Switcher(gcp).SwitchAsync("project-a", CancellationToken.None);

        gcp.CallsTo("GetProjectState").ShouldBeEmpty();
        gcp.SelectedProjectId.ShouldBe("project-a");
    }

    [Fact]
    public async Task The_list_holds_this_accounts_projects_its_run_projects_and_always_the_current_project()
    {
        var gcp = NewGcp();
        _store.Rows["chosen"] = new ProjectSettings("chosen");
        _store.Chosen[FakeGcp.DefaultAccountSub] = ["chosen"];
        _store.Rows["theirs"] = new ProjectSettings("theirs");
        _store.Chosen["someone-else"] = ["theirs"];
        await _runs.UpsertAsync(new RunRecord("own", JobPhase.Completed, Launch, Target: "cloud", ProjectId: "from-my-run", AccountSub: FakeGcp.DefaultAccountSub), CancellationToken.None);
        await _runs.UpsertAsync(new RunRecord("old", JobPhase.Completed, Launch, Target: "cloud", ProjectId: "from-old-row"), CancellationToken.None);
        await _runs.UpsertAsync(new RunRecord("other", JobPhase.Completed, Launch, Target: "cloud", ProjectId: "from-their-run", AccountSub: "someone-else"), CancellationToken.None);
        await _runs.UpsertAsync(new RunRecord("pc", JobPhase.Completed, Launch, Target: "local"), CancellationToken.None);

        var list = await Switcher(gcp).ListAsync(CancellationToken.None);

        list.Select(p => p.ProjectId).ShouldBe(["chosen", "from-my-run", "from-old-row", "project-a"], "a row with no account is listed under every account (#112); another account's run is not");
    }

    [Fact]
    public async Task A_remembered_bucket_is_shown_in_the_list_for_a_project_known_only_from_a_run()
    {
        var gcp = NewGcp();
        _store.Rows["from-my-run"] = new ProjectSettings("from-my-run", Bucket: "bucket-r");
        await _runs.UpsertAsync(new RunRecord("own", JobPhase.Completed, Launch, Target: "cloud", ProjectId: "from-my-run", AccountSub: FakeGcp.DefaultAccountSub), CancellationToken.None);

        (await Switcher(gcp).ListAsync(CancellationToken.None)).Single(p => p.ProjectId == "from-my-run").Bucket.ShouldBe("bucket-r");
    }

    // ---- r3 review: the project left, and the account that chose ----

    [Fact]
    public async Task A_project_selected_by_the_wizard_only_is_still_listed_after_switching_away_from_it()
    {
        // The wizard calls SelectProjectAsync and records nothing; before the fix the project being LEFT was never written, so it vanished from the list.
        var gcp = NewGcp("project-a");
        _store.Rows.ShouldBeEmpty("vacuity: nothing recorded project-a before the switch");

        await Switcher(gcp).SwitchAsync("project-b", CancellationToken.None);
        var list = await Switcher(gcp).ListAsync(CancellationToken.None);

        list.Select(p => p.ProjectId).ShouldBe(["project-a", "project-b"]);
        _store.Chosen[FakeGcp.DefaultAccountSub].ShouldContain("project-a", "recorded under the account that left it, not only shown because it was current");
    }

    [Fact]
    public async Task Listing_records_the_current_project_for_the_account_so_one_chosen_before_the_table_existed_is_kept()
    {
        var gcp = NewGcp("project-a");

        await Switcher(gcp).ListAsync(CancellationToken.None);

        _store.Chosen[FakeGcp.DefaultAccountSub].ShouldBe(["project-a"]);
    }

    [Fact]
    public async Task An_account_change_while_the_target_is_checked_selects_nothing_on_the_new_account_and_names_the_problem()
    {
        var gcp = NewGcp("project-a").WithAccount("sub-1", "a@example.test", "project-a").WithAccountProject("sub-1", "project-a");
        var setup = Substitute.For<IProjectSetupGateway>();
        setup.GetProjectStateAsync(Arg.Any<CloudScope>(), Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            await gcp.SwitchAccountAsync("sub-1", CancellationToken.None);
            return ProjectLifecycleState.Active;
        });

        var failure = await Should.ThrowAsync<AccountAuthException>(() => Switcher(gcp, setup).SwitchAsync("project-b", CancellationToken.None));

        failure.Code.ShouldBe(AuthErrorCodes.AccountChangedDuringSwitch);
        AuthErrorCodes.ActionResourceKey(failure.Code).ShouldBe("AuthAction_TryAgain");
        gcp.SelectedProjectId.ShouldBe("project-a", "the account now active keeps ITS project: project-b was checked as the other account");
        _store.Chosen.ShouldBeEmpty("no AccountProjects row was claimed by either account");
        _store.Rows.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_account_change_while_the_list_is_read_never_files_the_new_accounts_project_under_the_old_account()
    {
        // The card refreshes on exactly AccountChanged: the account (and its project) can change while the store is being read.
        var gcp = NewGcp("project-a").WithAccount("sub-2", "b@example.test", "project-z").WithAccountProject("sub-2", "project-z");
        _store.DuringGetKnown = () => gcp.SwitchAccountAsync("sub-2", CancellationToken.None).GetAwaiter().GetResult();

        await Switcher(gcp).ListAsync(CancellationToken.None);

        gcp.SelectedProjectId.ShouldBe("project-z", "vacuity: the account really changed during the read");
        _store.Chosen.Values.SelectMany(x => x).ShouldNotContain("project-z", "the new account's project is never written under the old sub");
        _store.Chosen.ContainsKey("sub-2").ShouldBeFalse();
    }

    [Fact]
    public async Task An_account_change_while_the_switch_is_being_saved_selects_nothing()
    {
        var gcp = NewGcp("project-a").WithAccount("sub-2", "b@example.test", "project-z").WithAccountProject("sub-2", "project-z");
        _store.DuringEnsure = () => gcp.SwitchAccountAsync("sub-2", CancellationToken.None).GetAwaiter().GetResult();

        var failure = await Should.ThrowAsync<AccountAuthException>(() => Switcher(gcp).SwitchAsync("project-b", CancellationToken.None));

        failure.Code.ShouldBe(AuthErrorCodes.AccountChangedDuringSwitch);
        gcp.SelectedProjectId.ShouldBe("project-z", "the account now active keeps ITS project");
    }

    [Fact]
    public async Task One_switch_reads_the_account_once_check_remember_and_select_all_use_that_one()
    {
        var gcp = NewGcp("project-a");

        await Switcher(gcp).SwitchAsync("project-b", CancellationToken.None);

        gcp.CallsTo("GetProjectState").Select(c => c.AccountSub).ShouldBe([FakeGcp.DefaultAccountSub]);
        _store.Chosen.Keys.ShouldBe([FakeGcp.DefaultAccountSub]);
        _store.Chosen[FakeGcp.DefaultAccountSub].OrderBy(x => x).ShouldBe(["project-a", "project-b"]);
    }

    // ---- #830 made every call on a run use the run's own project: a switch is never unsafe for a run, so it is never refused for one ----

    [Theory]
    [InlineData(JobPhase.Provisioning)]
    [InlineData(JobPhase.Running)]
    [InlineData(JobPhase.Uploading)]
    public async Task A_switch_is_allowed_while_a_run_in_the_old_project_is_unfinished_and_the_row_is_untouched(JobPhase phase)
    {
        var gcp = NewGcp();
        await _runs.UpsertAsync(new RunRecord("job-1", phase, Launch, Target: "cloud", ProjectId: "project-a", AccountSub: FakeGcp.DefaultAccountSub, VmName: "deg-job-1"), CancellationToken.None);

        await Switcher(gcp).SwitchAsync("project-b", CancellationToken.None);

        gcp.SelectedProjectId.ShouldBe("project-b");
        var row = _runs.AllRecordedInOrder.Single(r => r.JobId == "job-1");
        (row.Phase, row.ProjectId, row.AccountSub).ShouldBe((phase, "project-a", FakeGcp.DefaultAccountSub));
    }

    [Fact]
    public async Task A_kept_alive_computer_in_the_old_project_is_still_ended_in_that_project_after_a_switch()
    {
        // The observable: after a REAL switch (the account now points at project-b), the keep-alive expiry pass still finds and deletes the computer in project-a, as the row's account,
        // and makes no call about it in project-b. Before #830 this was why a switch had to be refused.
        var gcp = NewGcp("project-a").WithWorker(FakeWorkerMode.Never);
        var env = new JobReconcilerTests.Env(gcp);
        var request = await env.SeedAsync(
            "job-ka",
            JobPhase.Completed,
            vm: false,
            after: AfterTaskAction.KeepAlive,
            afterKeepAlive: AfterKeepAliveAction.Delete,
            keepAliveMinutes: 30,
            tweak: row => row with { FinishedAt = Launch.AddMinutes(-10), ProjectId = "project-a", AccountSub = FakeGcp.DefaultAccountSub });
        await gcp.CreateVmAsync(new CloudScope("project-a", FakeGcp.DefaultAccountSub), request.Spec with { ProjectId = "project-a" }, Zone, CancellationToken.None);

        await new ProjectSwitcher(gcp, _store, env.Repo, gcp).SwitchAsync("project-b", CancellationToken.None);
        gcp.SelectedProjectId.ShouldBe("project-b");
        _clock.Advance(TimeSpan.FromMinutes(21));

        var outcomes = await env.Reconciler(_clock, additionalSweepScopes: () => [gcp.SelectedScope()!]).EnforceLifecycleAsync(CancellationToken.None);

        outcomes.Single(o => o.JobId == "job-ka").Action.ShouldBe(LifecycleAction.VmDeleted);
        (await gcp.FindByJobIdAsync(new CloudScope("project-a", FakeGcp.DefaultAccountSub), "job-ka", CancellationToken.None)).ShouldBeEmpty("the computer is gone from project-a");
        var deletes = gcp.CallsTo("DeleteVm");
        deletes.ShouldNotBeEmpty();
        deletes.ShouldAllBe(c => c.ProjectId == "project-a" && c.AccountSub == FakeGcp.DefaultAccountSub);
        gcp.CallsTo("FindByJobId").Where(c => c.ProjectId == "project-b").ShouldBeEmpty("the run is not in project-b, and nothing looked for it there");
    }
}
