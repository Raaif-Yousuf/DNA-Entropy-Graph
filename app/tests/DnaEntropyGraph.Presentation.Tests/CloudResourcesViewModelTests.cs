using System.Text;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Cost;
using DnaEntropyGraph.Presentation.Services;
using DnaEntropyGraph.Presentation.ViewModels;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

/// <summary>
/// Issue #105, the Cloud page: the inventory by label with cost, Stop/Start/Delete per computer, and Delete everything with a confirm that names what will go.
/// The observables are the gateway calls (a button that calls nothing is wired to nothing) and the per-item report.
/// </summary>
public class CloudResourcesViewModelTests
{
    private const string Project = "dna-entropy-abc12345";
    private const string Mine = "inst-mine";
    private const string Other = "inst-other";
    private const string Bucket = "deg-bucket";

    private const string PricingJson = """
        {
          "schema": 1, "asOf": "2026-10-01", "currency": "USD", "region": "us-central1", "source": "test",
          "disk": { "type": "pd-balanced", "usdPerGbMonth": 0.10 },
          "machines": { "g2-standard-8": { "accelerator": "1x NVIDIA L4", "onDemandUsdPerHour": 0.85, "spotUsdPerHour": 0.18 } }
        }
        """;

    private readonly IGcpAccount _account = Substitute.For<IGcpAccount>();
    private readonly IDialogService _dialogs = Substitute.For<IDialogService>();
    private readonly IStringResourceProvider _strings = Substitute.For<IStringResourceProvider>();
    private readonly IComputeGateway _compute = Substitute.For<IComputeGateway>();
    private readonly IStorageGateway _storage = Substitute.For<IStorageGateway>();
    private readonly ISettingsStore _settings = Substitute.For<ISettingsStore>();
    private readonly IPricingSource _pricing = Substitute.For<IPricingSource>();
    private string? _confirmMessage;

    public CloudResourcesViewModelTests()
    {
        // A key plus the placeholders that matter, so a test sees both which string was chosen and what was put in it.
        _strings.GetString(Arg.Any<string>()).Returns(call => call.Arg<string>() + " {0}");
        _strings.GetString("CloudDeleteAll_Body").Returns("CloudDeleteAll_Body {0} {1} {2} {3}");
        _strings.GetString("CloudReport_DeletedRecoverable").Returns("CloudReport_DeletedRecoverable {0} {1}");
        _account.SelectedProjectId.Returns(Project);
        _settings.GetString(InstallationId.SettingsKey).Returns(Mine);
        _pricing.Load().Returns(PricingTable.Parse(PricingJson));
        _dialogs.ConfirmAsync(Arg.Any<string>(), Arg.Do<string>(m => _confirmMessage = m), Arg.Any<CancellationToken>()).Returns(true);
        Vms(Vm("deg-job-1", "RUNNING", Mine, "job-1"), Vm("deg-job-2", "TERMINATED", Mine, "job-2"));
        _compute.ListAppVmsAsync(Arg.Any<CancellationToken>()).Returns(call => Task.FromResult<IReadOnlyList<VmDescriptor>>(_all));
        _compute.ListDisksByInstallationAsync(Mine, Arg.Any<CancellationToken>()).Returns(call => Task.FromResult<IReadOnlyList<DiskDescriptor>>(
            [new DiskDescriptor("deg-job-2", "us-central1-a", 150, "READY", Labels(Mine), AttachedTo: _detached ? null : ["https://x/zones/us-central1-a/instances/deg-job-2"])]));
        _compute.When(c => c.DeleteVmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())).Do(_ => _detached = true);
        _storage.ListBucketsAsync(Project, Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<BucketDescriptor>>([new BucketDescriptor(Bucket, "US", Labels(Mine), null, 7)]));
        _storage.ListChildPrefixesAsync(Bucket, "jobs/", Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<string>>(["jobs/job-1/"]));
        _storage.TryDownloadAsync(Bucket, "jobs/job-1/manifest.json", Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<Stream?>(new MemoryStream(Encoding.UTF8.GetBytes("{\"createdBy\":{\"installationId\":\"" + Mine + "\"}}"))));
        _storage.GetPrefixUsageAsync(Bucket, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new PrefixUsage(3, 2_000_000));
    }

    private IReadOnlyList<VmDescriptor> _all = [];
    private bool _detached;

    private void Vms(params VmDescriptor[] vms)
    {
        _all = vms;
        _compute.ListByInstallationAsync(Mine, Arg.Any<CancellationToken>()).Returns(call => Task.FromResult<IReadOnlyList<VmDescriptor>>(_all.Where(v => v.Labels!["installation-id"] == Mine).ToList()));
    }

    private static Dictionary<string, string> Labels(string installation, string? job = null)
    {
        var labels = new Dictionary<string, string> { ["app"] = "dna-entropy-graph", ["installation-id"] = installation };
        if (job is not null)
        {
            labels["job-id"] = job;
        }

        return labels;
    }

    private static VmDescriptor Vm(string name, string status, string installation, string job)
        => new(name, "us-central1-a", status, Labels: Labels(installation, job), MachineType: "g2-standard-8");

    private CloudResourcesViewModel NewViewModel() => new(_account, _dialogs, _strings, _compute, new InstallationCleanup(_compute, _storage), _settings, _pricing);

    private async Task<CloudResourcesViewModel> LoadedAsync()
    {
        var viewModel = NewViewModel();
        await viewModel.RefreshCommand.ExecuteAsync(null);
        return viewModel;
    }

    [Fact]
    public async Task Refresh_lists_this_installations_computers_disks_and_job_folders_with_cost()
    {
        var viewModel = await LoadedAsync();

        viewModel.Vms.Select(v => v.Name).ShouldBe(["deg-job-1", "deg-job-2"]);
        viewModel.Vms[0].CostText.ShouldContain("CloudVm_CostRunning");
        viewModel.Vms[0].CostText.ShouldContain("0.85");
        viewModel.Vms[1].CostText.ShouldContain("CloudVm_CostStopped");
        viewModel.Vms[1].CostText.ShouldContain("0.49");
        viewModel.Disks.Select(d => d.Name).ShouldBe(["deg-job-2"]);
        viewModel.Disks[0].CostText.ShouldContain("0.49");
        viewModel.SummaryText.ShouldContain("CloudPage_Summary");
        viewModel.IsEmpty.ShouldBeFalse();
    }

    [Fact]
    public async Task A_computer_from_another_installation_is_listed_read_only_tagged_another_computer_with_no_buttons()
    {
        Vms(Vm("deg-job-1", "RUNNING", Mine, "job-1"), Vm("deg-theirs", "RUNNING", Other, "job-9"));

        var viewModel = await LoadedAsync();

        var theirs = viewModel.Vms.Single(v => v.Name == "deg-theirs");
        theirs.IsAnotherComputer.ShouldBeTrue();
        theirs.TagText.ShouldContain("CloudVm_AnotherComputer");
        theirs.HasActions.ShouldBeFalse();
        theirs.StopCommand.ShouldBeNull();
        theirs.DeleteCommand.ShouldBeNull();
        viewModel.Vms.Single(v => v.Name == "deg-job-1").HasActions.ShouldBeTrue();
    }

    [Fact]
    public async Task Stop_and_Start_call_the_gateway_for_that_computer_and_only_when_the_status_allows_it()
    {
        var viewModel = await LoadedAsync();
        var running = viewModel.Vms[0];
        var stopped = viewModel.Vms[1];

        running.StopCommand!.CanExecute(null).ShouldBeTrue();
        running.StartCommand!.CanExecute(null).ShouldBeFalse();
        stopped.StartCommand!.CanExecute(null).ShouldBeTrue();
        stopped.StopCommand!.CanExecute(null).ShouldBeFalse();

        await running.StopCommand.ExecuteAsync(null);
        await stopped.StartCommand.ExecuteAsync(null);

        await _compute.Received(1).StopVmAsync("deg-job-1", "us-central1-a", Arg.Any<CancellationToken>());
        await _compute.Received(1).StartVmAsync("deg-job-2", "us-central1-a", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Deleting_one_computer_asks_first_and_a_no_deletes_nothing()
    {
        var viewModel = await LoadedAsync();
        _dialogs.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);

        await viewModel.Vms[1].DeleteCommand!.ExecuteAsync(null);
        await _compute.DidNotReceiveWithAnyArgs().DeleteVmAsync(default!, default!, Arg.Any<CancellationToken>());

        _dialogs.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        await viewModel.Vms[1].DeleteCommand!.ExecuteAsync(null);
        await _compute.Received(1).DeleteVmAsync("deg-job-2", "us-central1-a", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_refused_stop_says_which_computer_and_names_the_action()
    {
        var viewModel = await LoadedAsync();
        _compute.StopVmAsync("deg-job-1", "us-central1-a", Arg.Any<CancellationToken>()).ThrowsAsync(new CloudOperationException(new CloudError("403", 403, "no"), CloudErrorKind.Permission));

        await viewModel.Vms[0].StopCommand!.ExecuteAsync(null);

        viewModel.HasError.ShouldBeTrue();
        viewModel.ErrorText.ShouldContain("CloudPage_ActionFailed");
        viewModel.ErrorText.ShouldContain("deg-job-1");
    }

    [Fact]
    public async Task Delete_everything_names_what_will_go_asks_first_and_declining_deletes_nothing()
    {
        Vms(Vm("deg-job-1", "RUNNING", Mine, "job-1"), Vm("deg-job-2", "TERMINATED", Mine, "job-2"), Vm("deg-theirs", "RUNNING", Other, "job-9"));
        var viewModel = await LoadedAsync();
        _dialogs.ConfirmAsync(Arg.Any<string>(), Arg.Do<string>(m => _confirmMessage = m), Arg.Any<CancellationToken>()).Returns(false);

        await viewModel.DeleteEverythingCommand.ExecuteAsync(null);

        // The whole formatted text: 2 computers (not the other installation's third), 1 disk, 1 job folder, 2 MB of results.
        _confirmMessage.ShouldBe("CloudDeleteAll_Body 2 1 1 CloudSize_MB 2.0");
        await _compute.DidNotReceiveWithAnyArgs().DeleteVmAsync(default!, default!, Arg.Any<CancellationToken>());
        await _storage.DidNotReceiveWithAnyArgs().DeletePrefixAsync(default!, default!, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Delete_everything_reports_each_item_with_its_outcome_including_the_soft_delete_retention()
    {
        Vms(Vm("deg-job-1", "RUNNING", Mine, "job-1"), Vm("deg-job-2", "TERMINATED", Mine, "job-2"), Vm("deg-theirs", "RUNNING", Other, "job-9"));
        var viewModel = await LoadedAsync();
        _compute.GetVmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<VmDescriptor?>(null));
        _compute.DeleteDiskAsync("deg-job-2", "us-central1-a", Arg.Any<CancellationToken>()).ThrowsAsync(new CloudOperationException(new CloudError("403", 403, "no"), CloudErrorKind.Permission));
        _storage.GetPrefixUsageAsync(Bucket, "jobs/job-1/", Arg.Any<CancellationToken>()).Returns(new PrefixUsage(0, 0));

        await viewModel.DeleteEverythingCommand.ExecuteAsync(null);

        await _compute.Received(1).DeleteVmAsync("deg-job-1", "us-central1-a", Arg.Any<CancellationToken>());
        await _compute.Received(1).DeleteVmAsync("deg-job-2", "us-central1-a", Arg.Any<CancellationToken>());
        await _compute.DidNotReceive().DeleteVmAsync("deg-theirs", Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _storage.Received(1).DeletePrefixAsync(Bucket, "jobs/job-1/", Arg.Any<CancellationToken>());
        viewModel.HasReport.ShouldBeTrue();
        viewModel.ReportHeadline.ShouldContain("CloudReport_Incomplete");
        viewModel.ReportLines.Count.ShouldBe(4);
        viewModel.ReportLines.ShouldContain(l => l.Contains("CloudReport_Failed") && l.Contains("deg-job-2"));
        viewModel.ReportLines.ShouldContain(l => l.Contains("CloudReport_DeletedRecoverable") && l.Contains("jobs/job-1/") && l.Contains('7'));
        viewModel.ReportLines.Count(l => l.Contains("CloudReport_Deleted ")).ShouldBe(2);
    }

    [Fact]
    public async Task Delete_everything_never_deletes_another_installations_computer_even_when_the_gateway_hands_it_over()
    {
        var stray = Vm("deg-theirs", "RUNNING", Other, "job-9");
        Vms(Vm("deg-job-1", "RUNNING", Mine, "job-1"), stray);
        _compute.ListByInstallationAsync(Mine, Arg.Any<CancellationToken>()).Returns(call => Task.FromResult<IReadOnlyList<VmDescriptor>>(_all));
        _compute.GetVmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<VmDescriptor?>(null));
        var viewModel = await LoadedAsync();

        await viewModel.DeleteEverythingCommand.ExecuteAsync(null);

        await _compute.Received(1).DeleteVmAsync("deg-job-1", "us-central1-a", Arg.Any<CancellationToken>());
        await _compute.DidNotReceive().DeleteVmAsync("deg-theirs", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_computer_created_while_the_confirm_dialog_was_open_is_left_alone_and_the_report_says_so()
    {
        // #740 finding 4: the dialog named two computers; a third appeared before the delete ran and must not go unnamed.
        var viewModel = await LoadedAsync();
        _compute.GetVmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<VmDescriptor?>(null));
        _dialogs.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            Vms(Vm("deg-job-1", "RUNNING", Mine, "job-1"), Vm("deg-job-2", "TERMINATED", Mine, "job-2"), Vm("deg-late", "RUNNING", Mine, "job-3"));
            return Task.FromResult(true);
        });

        await viewModel.DeleteEverythingCommand.ExecuteAsync(null);

        await _compute.Received(1).DeleteVmAsync("deg-job-1", "us-central1-a", Arg.Any<CancellationToken>());
        await _compute.Received(1).DeleteVmAsync("deg-job-2", "us-central1-a", Arg.Any<CancellationToken>());
        await _compute.DidNotReceive().DeleteVmAsync("deg-late", Arg.Any<string>(), Arg.Any<CancellationToken>());
        viewModel.ReportLines.ShouldContain(l => l.Contains("CloudReport_Skipped") && l.Contains("deg-late"));
        viewModel.ReportHeadline.ShouldContain("CloudReport_Incomplete");
    }

    [Fact]
    public async Task A_listing_that_fails_with_any_exception_shows_one_message_and_never_escapes_a_fire_and_forget_refresh()
    {
        _compute.ListAppVmsAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("parse"));
        var viewModel = NewViewModel();

        await viewModel.RefreshCommand.ExecuteAsync(null);

        viewModel.ErrorText.ShouldContain("CloudPage_ListFailed");
        viewModel.IsBusy.ShouldBeFalse();
        viewModel.IsEmpty.ShouldBeFalse();
    }

    [Fact]
    public async Task Delete_everything_survives_any_exception_in_the_listing_and_in_the_sweep_and_each_says_one_thing()
    {
        var viewModel = await LoadedAsync();
        _storage.ListBucketsAsync(Project, Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("parse"));

        await viewModel.DeleteEverythingCommand.ExecuteAsync(null);

        viewModel.ErrorText.ShouldContain("CloudPage_ListFailed");
        viewModel.IsBusy.ShouldBeFalse();
        await _compute.DidNotReceiveWithAnyArgs().DeleteVmAsync(default!, default!, Arg.Any<CancellationToken>());

        // Past the dialog: the sweep's own re-listing fails with something that is not a cloud error.
        var listing = Task.FromResult<IReadOnlyList<BucketDescriptor>>([new BucketDescriptor(Bucket, "US", Labels(Mine), null, 7)]);
        _storage.ListBucketsAsync(Project, Arg.Any<CancellationToken>()).Returns(_ => listing, _ => throw new InvalidOperationException("parse"));

        await viewModel.DeleteEverythingCommand.ExecuteAsync(null);

        viewModel.ErrorText.ShouldContain("CloudPage_DeleteAllInterrupted");
        viewModel.ErrorText.ShouldNotContain("CloudPage_DeleteAllFailed", Case.Sensitive, "an unknown failure may have struck mid-sweep, so the page must not say nothing was deleted");
        viewModel.IsBusy.ShouldBeFalse();
    }

    [Fact]
    public async Task A_failure_on_the_second_computer_still_shows_the_report_with_the_first_as_deleted_and_never_says_nothing_was_deleted()
    {
        // #740 round 2 (M1): the first computer is gone when the second one fails with something that is not a cloud error.
        var viewModel = await LoadedAsync();
        _compute.GetVmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<VmDescriptor?>(null));
        _compute.DeleteVmAsync("deg-job-2", Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns<Task>(_ => throw new InvalidOperationException("boom"));

        await viewModel.DeleteEverythingCommand.ExecuteAsync(null);

        viewModel.HasReport.ShouldBeTrue();
        viewModel.ReportHeadline.ShouldContain("CloudReport_Incomplete");
        viewModel.ReportLines.ShouldContain(l => l.Contains("CloudReport_Deleted ") && l.Contains("deg-job-1"));
        viewModel.ReportLines.ShouldContain(l => l.Contains("CloudReport_Failed") && l.Contains("deg-job-2"));
        viewModel.ErrorText.ShouldNotContain("CloudPage_DeleteAllFailed");
    }

    [Fact]
    public async Task A_report_from_an_earlier_delete_is_cleared_when_the_next_one_ends_early()
    {
        // #740 round 2 (M2): stale ReportLines and ReportHeadline must not sit under a new error.
        var viewModel = await LoadedAsync();
        _compute.GetVmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<VmDescriptor?>(null));
        await viewModel.DeleteEverythingCommand.ExecuteAsync(null);
        viewModel.HasReport.ShouldBeTrue();

        _dialogs.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);
        await viewModel.DeleteEverythingCommand.ExecuteAsync(null);
        viewModel.HasReport.ShouldBeFalse("the old report belongs to the old delete");
        viewModel.ReportLines.ShouldBeEmpty();

        _compute.ListByInstallationAsync(Mine, Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("x"));
        await viewModel.DeleteEverythingCommand.ExecuteAsync(null);
        viewModel.HasReport.ShouldBeFalse();
        viewModel.ErrorText.ShouldContain("CloudPage_ListFailed");
    }

    [Fact]
    public async Task A_cancelled_sweep_still_refreshes_the_lists_so_deleted_items_are_not_shown()
    {
        var viewModel = await LoadedAsync();
        var listsBefore = _compute.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IComputeGateway.ListAppVmsAsync));
        _compute.DeleteVmAsync("deg-job-2", Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns<Task>(_ => throw new OperationCanceledException());

        try
        {
            await viewModel.DeleteEverythingCommand.ExecuteAsync(null);
        }
        catch (OperationCanceledException)
        {
            // Either way: what matters is below.
        }

        viewModel.IsBusy.ShouldBeFalse();
        _compute.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IComputeGateway.ListAppVmsAsync)).ShouldBeGreaterThan(listsBefore, "the page must list again after a sweep that did anything");
    }

    [Fact]
    public async Task A_refused_stop_that_is_not_a_cloud_error_still_names_the_computer()
    {
        var viewModel = await LoadedAsync();
        _compute.StopVmAsync("deg-job-1", "us-central1-a", Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("x"));

        await viewModel.Vms[0].StopCommand!.ExecuteAsync(null);

        viewModel.ErrorText.ShouldContain("CloudPage_ActionFailed");
        viewModel.ErrorText.ShouldContain("deg-job-1");
    }

    [Fact]
    public async Task Sizes_come_from_the_string_table_not_from_unit_literals_in_code()
    {
        _strings.GetString("CloudPage_Summary").Returns("{0}|{1}|{2}|{3}");
        var viewModel = await LoadedAsync();

        viewModel.SummaryText.ShouldBe("1|CloudSize_MB 2.0|deg-bucket|CloudSize_MB 2.0");
    }

    [Fact]
    public async Task Without_a_chosen_project_nothing_is_called_and_the_page_says_to_set_up()
    {
        _account.SelectedProjectId.Returns((string?)null);
        var viewModel = NewViewModel();

        await viewModel.RefreshCommand.ExecuteAsync(null);

        viewModel.HasError.ShouldBeTrue();
        viewModel.ErrorText.ShouldContain("CloudPage_NoProject");
        await _compute.DidNotReceiveWithAnyArgs().ListAppVmsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_listing_that_fails_names_the_action_instead_of_showing_an_empty_page()
    {
        _compute.ListAppVmsAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new CloudOperationException(new CloudError("503", 503, "down"), CloudErrorKind.Network));
        var viewModel = NewViewModel();

        await viewModel.RefreshCommand.ExecuteAsync(null);

        viewModel.ErrorText.ShouldContain("CloudPage_ListFailed");
        viewModel.IsEmpty.ShouldBeFalse("an error is not an empty project");
    }

    [Fact]
    public async Task A_running_computer_raises_the_banner_and_a_clean_project_does_not()
    {
        (await LoadedAsync()).HasBanner.ShouldBeTrue();

        Vms(Vm("deg-job-2", "TERMINATED", Mine, "job-2"));
        (await LoadedAsync()).HasBanner.ShouldBeFalse();
    }

    // Issue #828: the page must never list or act on a project other than the one chosen NOW.
    private const string NewProject = "other-project-99";

    [Fact]
    public async Task After_a_project_switch_the_next_refresh_lists_the_new_project()
    {
        var viewModel = await LoadedAsync();
        _account.SelectedProjectId.Returns(NewProject);
        _storage.ListBucketsAsync(NewProject, Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<BucketDescriptor>>([]));

        await viewModel.RefreshCommand.ExecuteAsync(null);

        await _storage.Received(1).ListBucketsAsync(NewProject, Arg.Any<CancellationToken>());
        await _storage.Received().ListBucketsAsync(Project, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_project_switch_while_the_page_is_open_clears_the_old_rows_and_says_to_refresh()
    {
        var viewModel = await LoadedAsync();
        viewModel.Vms.Count.ShouldBe(2);
        _account.SelectedProjectId.Returns(NewProject);
        _storage.ListBucketsAsync(NewProject, Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<BucketDescriptor>>([]));
        Vms();

        _account.AccountChanged += Raise.Event<EventHandler>(_account, EventArgs.Empty);

        viewModel.Vms.ShouldBeEmpty();
        viewModel.Disks.ShouldBeEmpty();
        viewModel.ErrorText.ShouldContain("CloudPage_ProjectChanged");
        await _storage.DidNotReceive().ListBucketsAsync(NewProject, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Stop_and_Delete_on_a_row_listed_for_the_old_project_call_nothing_after_a_switch()
    {
        var viewModel = await LoadedAsync();
        var running = viewModel.Vms[0];
        var stopped = viewModel.Vms[1];
        _account.SelectedProjectId.Returns(NewProject);

        await running.StopCommand!.ExecuteAsync(null);
        await stopped.DeleteCommand!.ExecuteAsync(null);

        await _compute.DidNotReceiveWithAnyArgs().StopVmAsync(default!, default!, Arg.Any<CancellationToken>());
        await _compute.DidNotReceiveWithAnyArgs().DeleteVmAsync(default!, default!, Arg.Any<CancellationToken>());
        await _dialogs.DidNotReceiveWithAnyArgs().ConfirmAsync(default!, default!, Arg.Any<CancellationToken>());
        viewModel.ErrorText.ShouldContain("CloudPage_ProjectChanged");
    }

    [Fact]
    public async Task Delete_everything_aborts_when_the_project_changed_while_the_confirm_was_open()
    {
        var viewModel = await LoadedAsync();
        _dialogs.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            _account.SelectedProjectId.Returns(NewProject);
            return Task.FromResult(true);
        });

        await viewModel.DeleteEverythingCommand.ExecuteAsync(null);

        await _compute.DidNotReceiveWithAnyArgs().DeleteVmAsync(default!, default!, Arg.Any<CancellationToken>());
        await _storage.DidNotReceiveWithAnyArgs().DeletePrefixAsync(default!, default!, Arg.Any<CancellationToken>());
        viewModel.ErrorText.ShouldContain("CloudPage_ProjectChanged");
    }

    [Fact]
    public async Task A_listing_that_straddles_a_project_switch_is_discarded_not_shown()
    {
        var viewModel = NewViewModel();
        _compute.ListAppVmsAsync(Arg.Any<CancellationToken>()).Returns(call =>
        {
            _account.SelectedProjectId.Returns(NewProject);
            return Task.FromResult<IReadOnlyList<VmDescriptor>>(_all);
        });

        await viewModel.RefreshCommand.ExecuteAsync(null);

        viewModel.Vms.ShouldBeEmpty();
        viewModel.Disks.ShouldBeEmpty();
        // The discard says why itself (not the generic list-failed text, which also leaves the rows empty).
        viewModel.ErrorText.ShouldContain("CloudPage_ProjectChanged");
        viewModel.ErrorText.ShouldNotContain("CloudPage_ListFailed");
    }

    [Fact]
    public async Task A_straddling_listing_also_forgets_the_old_banner_and_summary()
    {
        var viewModel = await LoadedAsync();
        viewModel.BannerText.ShouldContain("CloudPage_RunningBanner", customMessage: "vacuity: the first listing set a banner");
        viewModel.SummaryText.ShouldContain("CloudPage_Summary", customMessage: "vacuity: the first listing set a summary");
        _compute.ListAppVmsAsync(Arg.Any<CancellationToken>()).Returns(call =>
        {
            _account.SelectedProjectId.Returns(NewProject);
            return Task.FromResult<IReadOnlyList<VmDescriptor>>(_all);
        });

        await viewModel.RefreshCommand.ExecuteAsync(null);

        viewModel.Vms.ShouldBeEmpty();
        viewModel.BannerText.ShouldBeEmpty();
        viewModel.SummaryText.ShouldBeEmpty();
        viewModel.ErrorText.ShouldContain("CloudPage_ProjectChanged");
    }

    [Fact]
    public async Task A_straddling_listing_also_forgets_the_old_empty_state()
    {
        Vms();
        _compute.ListDisksByInstallationAsync(Mine, Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<DiskDescriptor>>([]));
        _storage.ListBucketsAsync(Project, Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<BucketDescriptor>>([]));
        var viewModel = await LoadedAsync();
        viewModel.IsEmpty.ShouldBeTrue("vacuity: an empty project was listed");
        _compute.ListAppVmsAsync(Arg.Any<CancellationToken>()).Returns(call =>
        {
            _account.SelectedProjectId.Returns(NewProject);
            return Task.FromResult<IReadOnlyList<VmDescriptor>>(_all);
        });

        await viewModel.RefreshCommand.ExecuteAsync(null);

        viewModel.IsEmpty.ShouldBeFalse();
    }

    [Fact]
    public async Task A_switch_away_and_back_during_one_listing_still_discards_it()
    {
        var viewModel = NewViewModel();
        _compute.ListAppVmsAsync(Arg.Any<CancellationToken>()).Returns(call =>
        {
            // B and then A again while Google is answering: the id at the end equals the id at the start, but the answer may be for B.
            _account.SelectedProjectId.Returns(NewProject);
            _account.AccountChanged += Raise.Event<EventHandler>(_account, EventArgs.Empty);
            _account.SelectedProjectId.Returns(Project);
            _account.AccountChanged += Raise.Event<EventHandler>(_account, EventArgs.Empty);
            return Task.FromResult<IReadOnlyList<VmDescriptor>>(_all);
        });

        await viewModel.RefreshCommand.ExecuteAsync(null);

        viewModel.Vms.ShouldBeEmpty();
        viewModel.ErrorText.ShouldContain("CloudPage_ProjectChanged");
    }

    /// <summary>Like the real dispatcher adapter: every action is posted, none runs until the UI thread gets to it (here, until <see cref="Drain"/>).</summary>
    private sealed class DeferredDispatcher : IDispatcher
    {
        private readonly Queue<Action> _queue = new();

        public int Pending => _queue.Count;

        public void Enqueue(Action action) => _queue.Enqueue(action);

        public void Drain()
        {
            while (_queue.Count > 0)
            {
                _queue.Dequeue()();
            }
        }
    }

    private CloudResourcesViewModel NewDeferredViewModel(DeferredDispatcher dispatcher)
        => new(_account, _dialogs, _strings, _compute, new InstallationCleanup(_compute, _storage), _settings, _pricing, dispatcher);

    [Fact]
    public async Task With_a_posting_dispatcher_a_switch_away_and_back_before_the_handlers_run_still_discards_the_listing()
    {
        var dispatcher = new DeferredDispatcher();
        var viewModel = NewDeferredViewModel(dispatcher);
        _compute.ListAppVmsAsync(Arg.Any<CancellationToken>()).Returns(call =>
        {
            _account.SelectedProjectId.Returns(NewProject);
            _account.AccountChanged += Raise.Event<EventHandler>(_account, EventArgs.Empty);
            _account.SelectedProjectId.Returns(Project);
            _account.AccountChanged += Raise.Event<EventHandler>(_account, EventArgs.Empty);
            dispatcher.Pending.ShouldBe(2, "vacuity: neither handler has run yet");
            return Task.FromResult<IReadOnlyList<VmDescriptor>>(_all);
        });

        await viewModel.RefreshCommand.ExecuteAsync(null);

        viewModel.Vms.ShouldBeEmpty();
        viewModel.ErrorText.ShouldContain("CloudPage_ProjectChanged");
        dispatcher.Drain();
    }

    [Fact]
    public async Task With_a_posting_dispatcher_a_listing_started_after_a_switch_is_not_discarded_when_the_queued_handler_runs()
    {
        var dispatcher = new DeferredDispatcher();
        var viewModel = NewDeferredViewModel(dispatcher);
        _account.SelectedProjectId.Returns(NewProject);
        _account.AccountChanged += Raise.Event<EventHandler>(_account, EventArgs.Empty);
        dispatcher.Pending.ShouldBe(1, "vacuity: the handler is queued, not run");
        _storage.ListBucketsAsync(NewProject, Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<BucketDescriptor>>([]));
        _compute.ListAppVmsAsync(Arg.Any<CancellationToken>()).Returns(call =>
        {
            dispatcher.Drain();
            return Task.FromResult<IReadOnlyList<VmDescriptor>>(_all);
        });

        await viewModel.RefreshCommand.ExecuteAsync(null);

        viewModel.ErrorText.ShouldBeEmpty();
        viewModel.Vms.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_switch_cancels_every_stale_listing_even_after_another_refresh_finished()
    {
        var viewModel = await LoadedAsync();
        var gated = CancellationToken.None;
        var started = new TaskCompletionSource();
        var calls = 0;
        _compute.ListAppVmsAsync(Arg.Any<CancellationToken>()).Returns(call =>
        {
            if (Interlocked.Increment(ref calls) > 1)
            {
                return Task.FromResult<IReadOnlyList<VmDescriptor>>(_all);
            }

            gated = call.Arg<CancellationToken>();
            var pending = new TaskCompletionSource<IReadOnlyList<VmDescriptor>>();
            gated.Register(() => pending.TrySetCanceled(gated));
            started.SetResult();
            return pending.Task;
        });
        var slow = viewModel.RefreshCommand.ExecuteAsync(null);
        await started.Task;
        await viewModel.Vms[0].StopCommand!.ExecuteAsync(null);

        _account.SelectedProjectId.Returns(NewProject);
        _account.AccountChanged += Raise.Event<EventHandler>(_account, EventArgs.Empty);

        gated.IsCancellationRequested.ShouldBeTrue("the still-running listing is for the old project, though another refresh finished meanwhile");
        await Should.ThrowAsync<OperationCanceledException>(slow);
    }

    [Fact]
    public async Task After_Detach_an_account_change_no_longer_touches_the_view_model()
    {
        var viewModel = await LoadedAsync();
        viewModel.Detach();
        _account.SelectedProjectId.Returns(NewProject);

        _account.AccountChanged += Raise.Event<EventHandler>(_account, EventArgs.Empty);

        viewModel.Vms.Count.ShouldBe(2);
        viewModel.ErrorText.ShouldBeEmpty();
        viewModel.Detach();
    }

    [Fact]
    public async Task An_account_change_with_the_same_project_leaves_rows_and_error_alone()
    {
        var viewModel = await LoadedAsync();

        _account.AccountChanged += Raise.Event<EventHandler>(_account, EventArgs.Empty);

        viewModel.Vms.Count.ShouldBe(2);
        viewModel.ErrorText.ShouldBeEmpty();
    }

    [Fact]
    public void An_account_change_before_anything_was_listed_says_nothing()
    {
        var viewModel = NewViewModel();

        _account.AccountChanged += Raise.Event<EventHandler>(_account, EventArgs.Empty);

        viewModel.ErrorText.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_account_change_during_the_first_load_of_the_same_project_says_nothing()
    {
        var viewModel = NewViewModel();
        var gate = new TaskCompletionSource<IReadOnlyList<VmDescriptor>>();
        _compute.ListAppVmsAsync(Arg.Any<CancellationToken>()).Returns(_ => gate.Task);
        var load = viewModel.RefreshCommand.ExecuteAsync(null);

        _account.AccountChanged += Raise.Event<EventHandler>(_account, EventArgs.Empty);
        viewModel.ErrorText.ShouldBeEmpty();
        gate.SetResult(_all);
        await load;

        viewModel.ErrorText.ShouldBeEmpty();
        viewModel.Vms.Count.ShouldBe(2);
    }

    [Fact]
    public async Task An_account_change_after_a_failed_first_listing_says_nothing()
    {
        var viewModel = NewViewModel();
        _compute.ListAppVmsAsync(Arg.Any<CancellationToken>()).Returns<Task<IReadOnlyList<VmDescriptor>>>(_ => throw new InvalidOperationException("boom"));
        await viewModel.RefreshCommand.ExecuteAsync(null);
        viewModel.ErrorText.ShouldContain("CloudPage_ListFailed");

        _account.AccountChanged += Raise.Event<EventHandler>(_account, EventArgs.Empty);

        viewModel.ErrorText.ShouldContain("CloudPage_ListFailed");
        viewModel.ErrorText.ShouldNotContain("CloudPage_ProjectChanged");
    }

    [Fact]
    public async Task A_project_switch_cancels_the_listing_still_in_flight_so_Refresh_is_available_again()
    {
        var viewModel = NewViewModel();
        var seen = CancellationToken.None;
        var started = new TaskCompletionSource();
        _compute.ListAppVmsAsync(Arg.Any<CancellationToken>()).Returns(call =>
        {
            seen = call.Arg<CancellationToken>();
            var pending = new TaskCompletionSource<IReadOnlyList<VmDescriptor>>();
            seen.Register(() => pending.TrySetCanceled(seen));
            started.SetResult();
            return pending.Task;
        });
        var load = viewModel.RefreshCommand.ExecuteAsync(null);
        await started.Task;

        _account.SelectedProjectId.Returns(NewProject);
        _account.AccountChanged += Raise.Event<EventHandler>(_account, EventArgs.Empty);

        seen.IsCancellationRequested.ShouldBeTrue();
        await Should.ThrowAsync<OperationCanceledException>(load);
        viewModel.RefreshCommand.CanExecute(null).ShouldBeTrue();
        viewModel.IsBusy.ShouldBeFalse();
    }

    [Fact]
    public async Task An_account_change_for_the_same_project_does_not_cancel_the_listing_in_flight()
    {
        var viewModel = NewViewModel();
        var seen = CancellationToken.None;
        var gate = new TaskCompletionSource<IReadOnlyList<VmDescriptor>>();
        _compute.ListAppVmsAsync(Arg.Any<CancellationToken>()).Returns(call =>
        {
            seen = call.Arg<CancellationToken>();
            return gate.Task;
        });
        var load = viewModel.RefreshCommand.ExecuteAsync(null);

        _account.AccountChanged += Raise.Event<EventHandler>(_account, EventArgs.Empty);

        seen.IsCancellationRequested.ShouldBeFalse();
        gate.SetResult(_all);
        await load;
    }
}
