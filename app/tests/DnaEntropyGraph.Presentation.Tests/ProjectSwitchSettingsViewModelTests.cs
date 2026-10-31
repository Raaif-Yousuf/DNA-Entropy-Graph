using System.Text;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Presentation.Services;
using DnaEntropyGraph.Presentation.ViewModels;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

/// <summary>Issue #260: the project switcher card lists known projects, switches with no sign-in, and shows each project's remembered bucket.</summary>
public class ProjectSwitchSettingsViewModelTests
{
    private readonly IGcpAccount _account = Substitute.For<IGcpAccount>();
    private readonly IProjectSettingsStore _store = Substitute.For<IProjectSettingsStore>();
    private readonly IStringResourceProvider _strings = Substitute.For<IStringResourceProvider>();
    private readonly IRunRepository _runs = Substitute.For<IRunRepository>();
    private readonly IProjectSetupGateway _setup = Substitute.For<IProjectSetupGateway>();

    public ProjectSwitchSettingsViewModelTests()
    {
        _strings.GetString(Arg.Any<string>()).Returns(call => call.Arg<string>() + " {0}");
        _account.SelectedProjectId.Returns("project-a");
        _runs.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<RunRecord>>([]));
        _setup.GetProjectStateAsync(Arg.Any<CloudScope>(), Arg.Any<CancellationToken>()).Returns(ProjectLifecycleState.Active);
        _account.CurrentAccount.Returns(new AccountInfo("sub-1", "a@example.test"));
        _store.GetKnownAsync("sub-1", Arg.Any<CancellationToken>()).Returns([new ProjectSettings("project-a", Bucket: "bucket-a"), new ProjectSettings("project-b", Bucket: "bucket-b")]);
        _store.GetAsync("project-b", Arg.Any<CancellationToken>()).Returns(new ProjectSettings("project-b", Bucket: "bucket-b"));
        _account.SelectProjectAsync("project-b", Arg.Any<CancellationToken>()).Returns(Task.CompletedTask).AndDoes(_ => _account.SelectedProjectId.Returns("project-b"));
    }

    private ProjectSwitchSettingsViewModel NewViewModel() => new(new ProjectSwitcher(_account, _store, _runs, _setup), _account, _strings);

    [Fact]
    public async Task Refresh_lists_the_known_projects_and_shows_the_current_ones_bucket()
    {
        var vm = NewViewModel();

        await vm.RefreshCommand.ExecuteAsync(null);

        vm.ProjectIds.ShouldBe(["project-a", "project-b"]);
        vm.CurrentProjectId.ShouldBe("project-a");
        vm.BucketText.ShouldBe("ProjectBucketKnown bucket-a");
        vm.SwitchCommand.CanExecute(null).ShouldBeFalse("the chosen project is already the current one");
    }

    [Fact]
    public async Task Switching_selects_the_project_with_no_sign_in_and_shows_the_other_projects_bucket()
    {
        var vm = NewViewModel();
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.ChosenProjectId = "project-b";
        vm.SwitchCommand.CanExecute(null).ShouldBeTrue();

        await vm.SwitchCommand.ExecuteAsync(null);

        await _account.Received(1).SelectProjectAsync("project-b", Arg.Any<CancellationToken>());
        await _account.DidNotReceive().SignInAsync(Arg.Any<CancellationToken>());
        vm.CurrentProjectId.ShouldBe("project-b");
        vm.BucketText.ShouldContain("bucket-b");
        vm.Status.ShouldContain("project-b");
    }

    [Fact]
    public async Task A_run_still_going_in_the_old_project_does_not_stop_the_switch()
    {
        // #830: every call on a run uses the run's own project, so the old "refused while a run is going" rule is gone.
        _runs.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<RunRecord>>([new RunRecord("job-1", DnaEntropyGraph.Core.JobPhase.Running, DateTimeOffset.UtcNow, Target: "cloud", ProjectId: "project-a", AccountSub: "sub-1")]));
        var vm = NewViewModel();
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.ChosenProjectId = "project-b";

        await vm.SwitchCommand.ExecuteAsync(null);

        vm.CurrentProjectId.ShouldBe("project-b");
        vm.Status.ShouldNotContain("AuthError_");
    }

    [Fact]
    public async Task A_refused_switch_shows_the_named_message_with_its_action_and_keeps_the_current_project()
    {
        _setup.GetProjectStateAsync(Arg.Any<CloudScope>(), Arg.Any<CancellationToken>()).Returns(ProjectLifecycleState.NotFound);
        var vm = NewViewModel();
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.ChosenProjectId = "project-b";

        await vm.SwitchCommand.ExecuteAsync(null);

        vm.Status.ShouldContain("AuthError_PROJECT_UNREACHABLE");
        vm.Status.ShouldContain("AuthAction_ChooseAnotherProject");
        vm.CurrentProjectId.ShouldBe("project-a");
        await _account.DidNotReceive().SelectProjectAsync("project-b", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task No_connection_shows_the_network_message_with_try_again_not_the_project_message()
    {
        _setup.GetProjectStateAsync(Arg.Any<CloudScope>(), Arg.Any<CancellationToken>())
            .Returns<Task<ProjectLifecycleState>>(_ => throw new CloudOperationException(new CloudError("CLOUD_NOT_CONNECTED", null, "x"), CloudErrorKind.Other));
        var vm = NewViewModel();
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.ChosenProjectId = "project-b";

        await vm.SwitchCommand.ExecuteAsync(null);

        vm.Status.ShouldContain("AuthError_SIGNIN_NETWORK");
        vm.Status.ShouldContain("AuthAction_TryAgain");
        vm.Status.ShouldNotContain("PROJECT_UNREACHABLE");
        vm.CurrentProjectId.ShouldBe("project-a");
    }

    [Fact]
    public async Task A_store_failure_shows_the_try_again_message_and_the_account_keeps_its_project()
    {
        _store.EnsureAsync("project-b", "sub-1", Arg.Any<CancellationToken>()).Returns(Task.FromException(new IOException("disk")));
        var vm = NewViewModel();
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.ChosenProjectId = "project-b";

        await vm.SwitchCommand.ExecuteAsync(null);

        vm.Status.ShouldContain("AuthError_PROJECT_SAVE_FAILED");
        vm.Status.ShouldContain("AuthAction_TryAgain");
        vm.CurrentProjectId.ShouldBe("project-a");
        await _account.DidNotReceive().SelectProjectAsync("project-b", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_unexpected_failure_never_escapes_the_command_and_names_the_try_again_action()
    {
        // The command runs from a button: an exception left in it would crash the page or vanish (review: unguarded failures in SwitchAsync).
        _account.SelectProjectAsync("project-b", Arg.Any<CancellationToken>()).Returns(Task.FromException(new InvalidCastException("surprise")));
        var vm = NewViewModel();
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.ChosenProjectId = "project-b";

        await vm.SwitchCommand.ExecuteAsync(null);

        vm.Status.ShouldContain("ProjectSwitchFailed");
        vm.Status.ShouldContain("AuthAction_TryAgain");
        vm.CurrentProjectId.ShouldBe("project-a");
    }

    [Fact]
    public async Task A_sign_in_that_expired_shows_its_own_message_with_its_own_action()
    {
        _account.SelectProjectAsync("project-b", Arg.Any<CancellationToken>()).Returns(Task.FromException(new AccountAuthException(AuthErrorCodes.SigninExpired)));
        var vm = NewViewModel();
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.ChosenProjectId = "project-b";

        await vm.SwitchCommand.ExecuteAsync(null);

        vm.Status.ShouldContain("AuthError_SIGNIN_EXPIRED");
        vm.Status.ShouldContain("AuthAction_SignInAgain");
    }

    [Fact]
    public async Task A_failing_store_while_listing_sets_a_message_and_never_throws_out_of_the_command()
    {
        _store.GetKnownAsync("sub-1", Arg.Any<CancellationToken>()).Returns(Task.FromException<IReadOnlyList<ProjectSettings>>(new IOException("disk")));
        var vm = NewViewModel();

        await vm.RefreshCommand.ExecuteAsync(null);

        vm.Status.ShouldContain("ProjectListFailed");
    }

    // ---- r3 review: a list that follows the account, and a refresh nobody awaits that never fails unobserved ----

    private sealed class InlineDispatcher : IDispatcher
    {
        public void Enqueue(Action action) => action();
    }

    [Fact]
    public async Task The_list_follows_the_account_while_Settings_is_open_and_stops_after_Detach()
    {
        var vm = new ProjectSwitchSettingsViewModel(new ProjectSwitcher(_account, _store, _runs, _setup), _account, _strings, new InlineDispatcher());
        await vm.LoadAsync();
        vm.ProjectIds.ShouldBe(["project-a", "project-b"]);

        _account.CurrentAccount.Returns(new AccountInfo("sub-2", "b@example.test"));
        _account.SelectedProjectId.Returns("project-z");
        _store.GetKnownAsync("sub-2", Arg.Any<CancellationToken>()).Returns([new ProjectSettings("project-z", Bucket: "bucket-z")]);
        _account.AccountChanged += Raise.Event<EventHandler>(_account, EventArgs.Empty);

        await WaitUntilAsync(() => vm.CurrentProjectId == "project-z");
        vm.ProjectIds.ShouldBe(["project-z"]);
        vm.BucketText.ShouldContain("bucket-z");

        vm.Detach();
        var calls = _store.ReceivedCalls().Count();
        _account.AccountChanged += Raise.Event<EventHandler>(_account, EventArgs.Empty);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        _store.ReceivedCalls().Count().ShouldBe(calls, "a detached card no longer reacts to the account");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        condition().ShouldBeTrue("the condition did not become true in 2 seconds");
    }

    [Fact]
    public async Task Any_failure_while_listing_is_shown_as_a_message_and_never_faults_the_unawaited_refresh()
    {
        _store.GetKnownAsync("sub-1", Arg.Any<CancellationToken>()).Returns(Task.FromException<IReadOnlyList<ProjectSettings>>(new InvalidCastException("surprise")));
        var vm = NewViewModel();

        await vm.RefreshCommand.ExecuteAsync(null);
        var observed = vm.LoadAsync();
        await observed;

        observed.IsFaulted.ShouldBeFalse();
        vm.Status.ShouldContain("ProjectListFailed");
    }

    [Fact]
    public async Task A_cancelled_listing_is_not_an_error_message()
    {
        _store.GetKnownAsync("sub-1", Arg.Any<CancellationToken>()).Returns(Task.FromException<IReadOnlyList<ProjectSettings>>(new OperationCanceledException()));
        var vm = NewViewModel();

        await vm.LoadAsync();

        vm.Status.ShouldBeEmpty();
    }

    // ---- the observable of #260: the Cloud page follows the switch, with no browser ----

    [Fact]
    public async Task After_a_switch_the_Cloud_page_lists_the_other_projects_bucket_and_no_sign_in_was_asked()
    {
        const string Mine = "inst-mine";
        var compute = Substitute.For<IComputeGateway>();
        compute.ListAppVmsAsync(Arg.Any<CloudScope>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<VmDescriptor>>([]));
        compute.ListByInstallationAsync(Arg.Any<CloudScope>(), Mine, Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<VmDescriptor>>([]));
        compute.ListDisksByInstallationAsync(Arg.Any<CloudScope>(), Mine, Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<DiskDescriptor>>([]));
        var storage = Substitute.For<IStorageGateway>();
        var labels = new Dictionary<string, string> { ["app"] = "dna-entropy-graph", ["installation-id"] = Mine };
        storage.ListBucketsAsync(Arg.Any<CloudScope>(), Arg.Any<CancellationToken>()).Returns(call =>
            Task.FromResult<IReadOnlyList<BucketDescriptor>>([new BucketDescriptor("bucket-of-" + call.Arg<CloudScope>().ProjectId, "US", labels, null, 7)]));
        storage.ListChildPrefixesAsync(Arg.Any<CloudScope>(), Arg.Any<string>(), "jobs/", Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<string>>([]));
        storage.GetPrefixUsageAsync(Arg.Any<CloudScope>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new PrefixUsage(0, 0));
        storage.TryDownloadAsync(Arg.Any<CloudScope>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Stream?>(new MemoryStream(Encoding.UTF8.GetBytes("{}"))));
        var settings = Substitute.For<ISettingsStore>();
        settings.GetString(InstallationId.SettingsKey).Returns(Mine);
        _account.SelectedScope().Returns(call => TestScopes.Of(_account.SelectedProjectId));
        _strings.GetString("CloudPage_Summary").Returns("{0}|{1}|{2}|{3}");
        _strings.GetString("CloudPage_SummaryNoBucket").Returns("no-bucket|{0}");
        var cloudPage = new CloudResourcesViewModel(_account, Substitute.For<IDialogService>(), _strings, compute, new InstallationCleanup(compute, storage), settings);
        await cloudPage.RefreshCommand.ExecuteAsync(null);
        cloudPage.SummaryText.ShouldContain("bucket-of-project-a", customMessage: "vacuity: the page showed the first project's bucket before the switch");

        var vm = NewViewModel();
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.ChosenProjectId = "project-b";
        await vm.SwitchCommand.ExecuteAsync(null);
        await cloudPage.RefreshCommand.ExecuteAsync(null);

        cloudPage.SummaryText.ShouldContain("bucket-of-project-b");
        cloudPage.SummaryText.ShouldNotContain("bucket-of-project-a");
        await _account.DidNotReceive().SignInAsync(Arg.Any<CancellationToken>());
    }
}
