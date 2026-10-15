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
        var viewModel = await LoadedAsync();
        _dialogs.ConfirmAsync(Arg.Any<string>(), Arg.Do<string>(m => _confirmMessage = m), Arg.Any<CancellationToken>()).Returns(false);

        await viewModel.DeleteEverythingCommand.ExecuteAsync(null);

        _confirmMessage.ShouldNotBeNull();
        _confirmMessage.ShouldContain("CloudDeleteAll_Body");
        _confirmMessage.ShouldContain("2", Case.Sensitive);
        await _compute.DidNotReceiveWithAnyArgs().DeleteVmAsync(default!, default!, Arg.Any<CancellationToken>());
        await _storage.DidNotReceiveWithAnyArgs().DeletePrefixAsync(default!, default!, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Delete_everything_reports_each_item_with_its_outcome_including_the_soft_delete_retention()
    {
        var viewModel = await LoadedAsync();
        _compute.GetVmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<VmDescriptor?>(null));
        _compute.DeleteDiskAsync("deg-job-2", "us-central1-a", Arg.Any<CancellationToken>()).ThrowsAsync(new CloudOperationException(new CloudError("403", 403, "no"), CloudErrorKind.Permission));
        _storage.GetPrefixUsageAsync(Bucket, "jobs/job-1/", Arg.Any<CancellationToken>()).Returns(new PrefixUsage(0, 0));

        await viewModel.DeleteEverythingCommand.ExecuteAsync(null);

        await _compute.Received(1).DeleteVmAsync("deg-job-1", "us-central1-a", Arg.Any<CancellationToken>());
        await _compute.Received(1).DeleteVmAsync("deg-job-2", "us-central1-a", Arg.Any<CancellationToken>());
        await _storage.Received(1).DeletePrefixAsync(Bucket, "jobs/job-1/", Arg.Any<CancellationToken>());
        viewModel.HasReport.ShouldBeTrue();
        viewModel.ReportHeadline.ShouldContain("CloudReport_Incomplete");
        viewModel.ReportLines.Count.ShouldBe(4);
        viewModel.ReportLines.ShouldContain(l => l.Contains("CloudReport_Failed") && l.Contains("deg-job-2"));
        viewModel.ReportLines.ShouldContain(l => l.Contains("CloudReport_DeletedRecoverable") && l.Contains("jobs/job-1/") && l.Contains('7'));
        viewModel.ReportLines.Count(l => l.Contains("CloudReport_Deleted ")).ShouldBe(2);
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
}
