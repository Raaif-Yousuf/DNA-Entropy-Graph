using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Setup;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests.Setup;

/// <summary>Issue #99: the setup steps behind the first-run wizard (project, billing, services, storage and identity, GPU allowance). Every failure becomes a code with one action; nothing throws at the caller.</summary>
public class SetupCheckServiceTests
{
    private const string Project = "dna-entropy-abc12345";

    private readonly IProjectCatalogGateway _projects = Substitute.For<IProjectCatalogGateway>();
    private readonly IBillingGateway _billing = Substitute.For<IBillingGateway>();
    private readonly IServiceEnablementGateway _services = Substitute.For<IServiceEnablementGateway>();
    private readonly IStorageGateway _storage = Substitute.For<IStorageGateway>();
    private readonly IWorkerIdentityGateway _identity = Substitute.For<IWorkerIdentityGateway>();
    private readonly IQuotaGateway _quota = Substitute.For<IQuotaGateway>();

    private SetupCheckService NewService() => new(_projects, _billing, new BillingSetup(_billing), _services, _storage, _identity, _quota);

    private static CloudOperationException Denied(string? code = null, int status = 403, string message = "denied")
    {
        var error = new CloudError(code, status, message);
        return new CloudOperationException(error, CloudErrorClassifier.Classify(error));
    }

    // ---- project ----

    [Fact]
    public async Task An_active_project_is_ok()
    {
        _projects.GetProjectAsync(Project, Arg.Any<CancellationToken>()).Returns(new ProjectSummary(Project, "P", ProjectLifecycleState.Active, true));

        var result = await NewService().CheckProjectAsync(Project, TestContext.Current.CancellationToken);

        result.Step.ShouldBe(SetupStep.Project);
        result.Status.ShouldBe(SetupStatus.Ok);
    }

    [Theory]
    [InlineData(ProjectLifecycleState.NotFound)]
    [InlineData(ProjectLifecycleState.Other)]
    public async Task A_missing_or_deleted_project_asks_for_another_project(ProjectLifecycleState state)
    {
        _projects.GetProjectAsync(Project, Arg.Any<CancellationToken>()).Returns(state == ProjectLifecycleState.NotFound ? null : new ProjectSummary(Project, "P", state, false));

        var result = await NewService().CheckProjectAsync(Project, TestContext.Current.CancellationToken);

        result.Status.ShouldBe(SetupStatus.NeedsAction);
        result.Code.ShouldBe(AuthErrorCodes.ProjectInvalid);
        result.Action.Kind.ShouldBe(SetupActionKind.PickProject);
    }

    [Fact]
    public async Task Listing_projects_returns_them_and_a_failure_is_a_result_not_a_throw()
    {
        var listed = new[] { new ProjectSummary("p1", "One", ProjectLifecycleState.Active, true) };
        _projects.ListActiveProjectsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<ProjectSummary>>(listed), Task.FromException<IReadOnlyList<ProjectSummary>>(new CloudOperationException(new CloudError("PERMISSION_DENIED", 403, "no"), CloudErrorKind.Permission)));

        var ok = await NewService().ListProjectsAsync(TestContext.Current.CancellationToken);
        ok.Status.ShouldBe(SetupStatus.Ok);
        ok.Projects.ShouldBe(listed);

        var failed = await NewService().ListProjectsAsync(TestContext.Current.CancellationToken);
        failed.Status.ShouldBe(SetupStatus.NeedsAction);
        failed.Code.ShouldBe(SetupErrorCodes.Permission);
    }

    [Fact]
    public async Task Creating_a_project_returns_it_and_names_the_limit_when_google_refuses()
    {
        _projects.CreateProjectAsync(Project, ProjectIdGenerator.DisplayName, "inst-1", Arg.Any<CancellationToken>())
            .Returns(new ProjectSummary(Project, ProjectIdGenerator.DisplayName, ProjectLifecycleState.Active, true));
        var ok = await NewService().CreateProjectAsync(Project, "inst-1", TestContext.Current.CancellationToken);
        ok.Status.ShouldBe(SetupStatus.Ok);
        ok.ProjectId.ShouldBe(Project);

        _projects.CreateProjectAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<ProjectSummary>>(_ => throw Denied(SetupErrorCodes.ProjectQuota, 429));
        var limit = await NewService().CreateProjectAsync(Project, "inst-1", TestContext.Current.CancellationToken);
        limit.Status.ShouldBe(SetupStatus.NeedsAction);
        limit.Code.ShouldBe(SetupErrorCodes.ProjectQuota);
        limit.Action.Kind.ShouldBe(SetupActionKind.PickProject);
    }

    // ---- billing ----

    [Fact]
    public async Task Billing_already_on_is_ok_and_names_the_account()
    {
        _billing.GetBillingStatusAsync(Project, Arg.Any<CancellationToken>()).Returns(new BillingStatus(true, "billingAccounts/1"));

        var result = await NewService().CheckBillingAsync(Project, TestContext.Current.CancellationToken);

        result.Status.ShouldBe(SetupStatus.Ok);
        result.AccountId.ShouldBe("billingAccounts/1");
    }

    [Fact]
    public async Task No_billing_account_opens_googles_page_and_the_same_call_is_the_recheck()
    {
        _billing.GetBillingStatusAsync(Project, Arg.Any<CancellationToken>()).Returns(new BillingStatus(false, null));
        _billing.ListOpenBillingAccountsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<BillingAccountSummary>());

        var result = await NewService().CheckBillingAsync(Project, TestContext.Current.CancellationToken);

        result.Status.ShouldBe(SetupStatus.NeedsAction);
        result.Code.ShouldBe(SetupErrorCodes.NoBilling);
        result.Action.Kind.ShouldBe(SetupActionKind.OpenLink);
        result.Link.ShouldBe(BillingLinks.ForProject(Project));
    }

    [Fact]
    public async Task Several_accounts_ask_the_user_to_pick_one()
    {
        var accounts = new[] { new BillingAccountSummary("a1", "One"), new BillingAccountSummary("a2", "Two") };
        _billing.GetBillingStatusAsync(Project, Arg.Any<CancellationToken>()).Returns(new BillingStatus(false, null));
        _billing.ListOpenBillingAccountsAsync(Arg.Any<CancellationToken>()).Returns(accounts);

        var result = await NewService().CheckBillingAsync(Project, TestContext.Current.CancellationToken);

        result.Status.ShouldBe(SetupStatus.NeedsAction);
        result.Action.Kind.ShouldBe(SetupActionKind.PickBillingAccount);
        result.Accounts.Select(a => a.AccountId).ShouldBe(["a1", "a2"]);
        result.Code.ShouldBeNull();
    }

    [Fact]
    public async Task A_linked_but_off_account_with_other_accounts_to_pick_names_why_with_BILLING_STILL_OFF()
    {
        var accounts = new[] { new BillingAccountSummary("a1", "One"), new BillingAccountSummary("a2", "Two") };
        _billing.GetBillingStatusAsync(Project, Arg.Any<CancellationToken>()).Returns(new BillingStatus(false, "a1"));
        _billing.ListOpenBillingAccountsAsync(Arg.Any<CancellationToken>()).Returns(accounts);

        var result = await NewService().CheckBillingAsync(Project, TestContext.Current.CancellationToken);

        result.Action.Kind.ShouldBe(SetupActionKind.PickBillingAccount);
        result.Code.ShouldBe(SetupErrorCodes.BillingStillOff);
        result.Accounts.Select(a => a.AccountId).ShouldBe(["a2"]);
    }

    [Fact]
    public async Task A_permission_failure_listing_accounts_after_the_link_reaches_the_step_as_the_permission_code()
    {
        _billing.GetBillingStatusAsync(Project, Arg.Any<CancellationToken>()).Returns(new BillingStatus(false, "a1"));
        _billing.ListOpenBillingAccountsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<BillingAccountSummary>>(new CloudOperationException(new CloudError(SetupErrorCodes.Permission, 403, "denied"), CloudErrorKind.Permission)));

        var result = await NewService().LinkBillingAsync(Project, "a1", TestContext.Current.CancellationToken);

        result.Status.ShouldBe(SetupStatus.NeedsAction);
        result.Code.ShouldBe(SetupErrorCodes.Permission);
    }

    [Fact]
    public async Task Linking_the_chosen_account_turns_billing_on()
    {
        _billing.GetBillingStatusAsync(Project, Arg.Any<CancellationToken>()).Returns(new BillingStatus(true, "a2"));

        var result = await NewService().LinkBillingAsync(Project, "a2", TestContext.Current.CancellationToken);

        await _billing.Received(1).LinkProjectAsync(Project, "a2", Arg.Any<CancellationToken>());
        result.Status.ShouldBe(SetupStatus.Ok);
        result.AccountId.ShouldBe("a2");
    }

    [Fact]
    public async Task A_missing_billing_right_on_the_chosen_account_keeps_that_account_for_the_request_text()
    {
        // The #51 review: BillingRequestText.Fill needs the account the user chose, and the exception only carries it in prose.
        _billing.LinkProjectAsync(Project, "a2", Arg.Any<CancellationToken>()).Returns<Task>(_ => throw Denied(SetupErrorCodes.BillingNoPermission));

        var result = await NewService().LinkBillingAsync(Project, "a2", TestContext.Current.CancellationToken);

        result.Status.ShouldBe(SetupStatus.NeedsAction);
        result.Code.ShouldBe(SetupErrorCodes.BillingNoPermission);
        result.Action.Kind.ShouldBe(SetupActionKind.CopyText);
        result.AccountId.ShouldBe("a2");
    }

    [Fact]
    public async Task A_missing_billing_right_on_the_only_account_keeps_that_account_too()
    {
        // BillingSetup.EnsureAsync links the single open account by itself, so the exception arrives with no account chosen.
        _billing.GetBillingStatusAsync(Project, Arg.Any<CancellationToken>()).Returns(new BillingStatus(false, null));
        _billing.ListOpenBillingAccountsAsync(Arg.Any<CancellationToken>()).Returns(new[] { new BillingAccountSummary("only-one", "Lab") });
        _billing.LinkProjectAsync(Project, "only-one", Arg.Any<CancellationToken>()).Returns<Task>(_ => throw Denied(SetupErrorCodes.BillingNoPermission));

        var result = await NewService().CheckBillingAsync(Project, TestContext.Current.CancellationToken);

        result.Code.ShouldBe(SetupErrorCodes.BillingNoPermission);
        result.AccountId.ShouldBe("only-one");
    }

    // ---- services ----

    [Fact]
    public async Task Services_are_turned_on_in_the_documented_order()
    {
        var result = await NewService().EnableServicesAsync(Project, TestContext.Current.CancellationToken);

        await _services.Received(1).EnableServicesAsync(Project, Arg.Is<IReadOnlyList<string>>(ids => ids.SequenceEqual(RequiredServices.Ids)), Arg.Any<CancellationToken>());
        result.Status.ShouldBe(SetupStatus.Ok);
    }

    [Fact]
    public async Task A_member_who_is_not_the_owner_is_told_to_create_a_project_of_their_own()
    {
        _services.EnableServicesAsync(Project, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw Denied(SetupErrorCodes.NotProjectOwner));

        var result = await NewService().EnableServicesAsync(Project, TestContext.Current.CancellationToken);

        result.Code.ShouldBe(SetupErrorCodes.NotProjectOwner);
        result.Action.Kind.ShouldBe(SetupActionKind.CreateProject);
    }

    // ---- storage and identity ----

    [Fact]
    public async Task Storage_makes_the_bucket_then_the_worker_identity_on_that_bucket()
    {
        _storage.EnsureBucketAsync(Project, Arg.Any<CancellationToken>()).Returns("deg-1-abcdef");
        _identity.EnsureWorkerIdentityAsync(Project, "deg-1-abcdef", Arg.Any<CancellationToken>()).Returns(new WorkerIdentity("w@p.iam", null));

        var result = await NewService().EnsureStorageAsync(Project, TestContext.Current.CancellationToken);

        result.Status.ShouldBe(SetupStatus.Ok);
        result.Bucket.ShouldBe("deg-1-abcdef");
        result.NoteCode.ShouldBeNull();
    }

    [Fact]
    public async Task The_default_account_stand_in_is_ok_with_a_note_and_a_continue_action()
    {
        _storage.EnsureBucketAsync(Project, Arg.Any<CancellationToken>()).Returns("deg-1-abcdef");
        _identity.EnsureWorkerIdentityAsync(Project, "deg-1-abcdef", Arg.Any<CancellationToken>())
            .Returns(new WorkerIdentity("1-compute@developer", SetupErrorCodes.WorkerDefaultAccount));

        var result = await NewService().EnsureStorageAsync(Project, TestContext.Current.CancellationToken);

        result.Status.ShouldBe(SetupStatus.Ok);
        result.NoteCode.ShouldBe(SetupErrorCodes.WorkerDefaultAccount);
    }

    [Fact]
    public async Task A_bucket_that_was_not_configured_as_asked_is_retryable_and_never_reaches_the_identity_step()
    {
        _storage.EnsureBucketAsync(Project, Arg.Any<CancellationToken>()).Returns<Task<string>>(_ => throw Denied(SetupErrorCodes.BucketConfigNotApplied, 500));

        var result = await NewService().EnsureStorageAsync(Project, TestContext.Current.CancellationToken);

        result.Code.ShouldBe(SetupErrorCodes.BucketConfigNotApplied);
        result.Action.Kind.ShouldBe(SetupActionKind.Retry);
        await _identity.DidNotReceiveWithAnyArgs().EnsureWorkerIdentityAsync(default!, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_missing_default_account_links_to_the_service_accounts_page()
    {
        _storage.EnsureBucketAsync(Project, Arg.Any<CancellationToken>()).Returns("deg-1-abcdef");
        _identity.EnsureWorkerIdentityAsync(Project, "deg-1-abcdef", Arg.Any<CancellationToken>())
            .Returns<Task<WorkerIdentity>>(_ => throw Denied(SetupErrorCodes.WorkerDefaultAccountMissing));

        var result = await NewService().EnsureStorageAsync(Project, TestContext.Current.CancellationToken);

        result.Action.Kind.ShouldBe(SetupActionKind.OpenLink);
        result.Link.ShouldBe(ServiceAccountLinks.ForProject(Project));
    }

    // ---- GPU allowance ----

    [Fact]
    public async Task A_tier_with_quota_in_some_regions_is_allowed_and_counts_them()
    {
        var regions = new Dictionary<string, IReadOnlyList<QuotaMetric>>
        {
            ["us-central1"] = [new QuotaMetric(QuotaMetrics.NvidiaL4Gpus, 1, 0)],
            ["europe-west4"] = [new QuotaMetric(QuotaMetrics.NvidiaL4Gpus, 1, 0)],
            ["asia-east1"] = [new QuotaMetric(QuotaMetrics.NvidiaL4Gpus, 0, 0)],
        };
        _quota.ReadQuotaAsync(Project, Arg.Any<CancellationToken>()).Returns(new QuotaReadResult(regions, new QuotaMetric(QuotaMetrics.GpusAllRegions, 1, 0), []));

        var result = await NewService().CheckQuotaAsync(Project, TestContext.Current.CancellationToken);

        result.Status.ShouldBe(SetupStatus.Ok);
        var l4 = result.Tiers.Single(t => t.Metric == QuotaMetrics.NvidiaL4Gpus);
        l4.AllowedRegions.ShouldBe(2);
        l4.State.ShouldBe(QuotaState.Available);
        result.Link.ShouldBe(QuotaLinks.ForProject(Project));
    }

    [Fact]
    public async Task No_quota_anywhere_needs_action_but_skipping_is_allowed()
    {
        var regions = new Dictionary<string, IReadOnlyList<QuotaMetric>> { ["us-central1"] = [new QuotaMetric(QuotaMetrics.NvidiaL4Gpus, 0, 0)] };
        _quota.ReadQuotaAsync(Project, Arg.Any<CancellationToken>()).Returns(new QuotaReadResult(regions, new QuotaMetric(QuotaMetrics.GpusAllRegions, 0, 0), []));

        var result = await NewService().CheckQuotaAsync(Project, TestContext.Current.CancellationToken);

        result.Status.ShouldBe(SetupStatus.NeedsAction);
        result.Tiers.Single(t => t.Metric == QuotaMetrics.NvidiaL4Gpus).State.ShouldBe(QuotaState.None);
        result.Action.Kind.ShouldBe(SetupActionKind.OpenLink);
        result.CanSkip.ShouldBeTrue();
    }

    [Fact]
    public async Task A_quota_read_that_failed_is_unknown_never_zero()
    {
        var failure = new QuotaReadFailure("regions.list", new CloudError(null, 403, "no"), CloudErrorKind.Permission);
        _quota.ReadQuotaAsync(Project, Arg.Any<CancellationToken>()).Returns(QuotaReadResult.Failed(failure));

        var result = await NewService().CheckQuotaAsync(Project, TestContext.Current.CancellationToken);

        result.Status.ShouldBe(SetupStatus.NeedsAction);
        result.Tiers.ShouldAllBe(t => t.State == QuotaState.Unknown);
        result.Code.ShouldBe(SetupErrorCodes.Permission);
    }

    [Theory]
    [InlineData(CloudErrorKind.Permission)]
    [InlineData(CloudErrorKind.OrgPolicy)]
    [InlineData(CloudErrorKind.ApiDisabled)]
    [InlineData(CloudErrorKind.Billing)]
    public async Task A_quota_read_that_hits_an_abort_class_error_names_its_action_and_cannot_be_skipped(CloudErrorKind kind)
    {
        // CLAUDE.md Critical Pitfalls: billing, api_disabled, permission and org_policy abort immediately with a named action.
        var failure = new QuotaReadFailure("regions.list", new CloudError(null, 403, "no"), kind);
        _quota.ReadQuotaAsync(Project, Arg.Any<CancellationToken>()).Returns(QuotaReadResult.Failed(failure));

        var read = await NewService().CheckQuotaAsync(Project, TestContext.Current.CancellationToken);
        read.CanSkip.ShouldBeFalse();
        read.Code.ShouldNotBeNull();
        read.Action.Kind.ShouldNotBe(SetupActionKind.None);

        _quota.ReadQuotaAsync(Project, Arg.Any<CancellationToken>()).Returns<Task<QuotaReadResult>>(_ => throw new CloudOperationException(new CloudError(null, 403, "no"), kind));
        var thrown = await NewService().CheckQuotaAsync(Project, TestContext.Current.CancellationToken);
        thrown.CanSkip.ShouldBeFalse();
        thrown.Code.ShouldBe(read.Code);
    }

    [Fact]
    public async Task A_quota_read_that_failed_for_an_unclassified_reason_can_be_retried_or_skipped()
    {
        var failure = new QuotaReadFailure("regions.list", new CloudError(null, 503, "down"), CloudErrorKind.Network);
        _quota.ReadQuotaAsync(Project, Arg.Any<CancellationToken>()).Returns(QuotaReadResult.Failed(failure));

        var result = await NewService().CheckQuotaAsync(Project, TestContext.Current.CancellationToken);

        result.Code.ShouldBeNull();
        result.CanSkip.ShouldBeTrue();
        result.Action.Kind.ShouldBe(SetupActionKind.Retry);
    }

    [Fact]
    public async Task An_all_regions_cap_of_zero_overrides_a_regional_allowance_of_one()
    {
        // CLAUDE.md: GPUS_ALL_REGIONS=0 overrides a regional 1.
        var regions = new Dictionary<string, IReadOnlyList<QuotaMetric>> { ["us-central1"] = [new QuotaMetric(QuotaMetrics.NvidiaL4Gpus, 1, 0)] };
        _quota.ReadQuotaAsync(Project, Arg.Any<CancellationToken>()).Returns(new QuotaReadResult(regions, new QuotaMetric(QuotaMetrics.GpusAllRegions, 0, 0), []));

        var result = await NewService().CheckQuotaAsync(Project, TestContext.Current.CancellationToken);

        result.Status.ShouldBe(SetupStatus.NeedsAction);
        result.Tiers.Single(t => t.Metric == QuotaMetrics.NvidiaL4Gpus).State.ShouldBe(QuotaState.None);
        result.CanSkip.ShouldBeTrue();
    }

    [Fact]
    public async Task A_regional_allowance_with_an_unread_all_regions_cap_is_unknown_not_zero()
    {
        var regions = new Dictionary<string, IReadOnlyList<QuotaMetric>> { ["us-central1"] = [new QuotaMetric(QuotaMetrics.NvidiaL4Gpus, 1, 0), new QuotaMetric(QuotaMetrics.NvidiaA100Gpus, 1, 0)] };
        _quota.ReadQuotaAsync(Project, Arg.Any<CancellationToken>()).Returns(new QuotaReadResult(regions, null, []));

        var result = await NewService().CheckQuotaAsync(Project, TestContext.Current.CancellationToken);

        result.Tiers.ShouldAllBe(t => t.State == QuotaState.Unknown);
        result.Action.Kind.ShouldBe(SetupActionKind.Retry);
        result.CanSkip.ShouldBeTrue();
    }

    // ---- the error mapping every step shares ----

    [Theory]
    [InlineData(SetupErrorCodes.ProjectQuota, null, SetupErrorCodes.ProjectQuota)]
    [InlineData(null, CloudErrorKind.Permission, SetupErrorCodes.Permission)]
    [InlineData(null, CloudErrorKind.OrgPolicy, SetupErrorCodes.OrgPolicyBlock)]
    [InlineData(null, CloudErrorKind.ApiDisabled, SetupErrorCodes.ApiDisabled)]
    [InlineData(null, CloudErrorKind.Billing, SetupErrorCodes.NoBilling)]
    [InlineData(null, CloudErrorKind.Network, null)]
    [InlineData("NOT_A_SETUP_CODE", CloudErrorKind.Other, null)]
    public void Errors_map_to_a_setup_code_or_to_the_catch_all(string? code, CloudErrorKind? kind, string? expected)
    {
        var exception = new CloudOperationException(new CloudError(code, 400, "x"), kind ?? CloudErrorKind.Other);

        SetupErrorMapper.CodeFor(exception).ShouldBe(expected);
    }

    [Fact]
    public async Task Cancellation_is_never_swallowed()
    {
        _services.EnableServicesAsync(Project, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns<Task>(_ => throw new OperationCanceledException());

        await Should.ThrowAsync<OperationCanceledException>(() => NewService().EnableServicesAsync(Project, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Every_setup_code_has_an_action_kind_so_no_message_is_a_dead_end()
    {
        foreach (var code in SetupErrorCodes.All)
        {
            SetupActions.KindFor(code).ShouldNotBe(SetupActionKind.None, code);
        }
    }
}
