using DnaEntropyGraph.Core.Cloud;

namespace DnaEntropyGraph.Core.Setup;

/// <summary>What the wizard (and the Settings health report) asks of the cloud, one step at a time. Idempotent: every call is also the re-check.</summary>
public interface ISetupCheckService
{
    Task<SetupStepResult> ListProjectsAsync(CancellationToken cancellationToken);

    Task<SetupStepResult> CreateProjectAsync(string projectId, string installationId, CancellationToken cancellationToken);

    Task<SetupStepResult> CheckProjectAsync(string projectId, CancellationToken cancellationToken);

    Task<SetupStepResult> CheckBillingAsync(string projectId, CancellationToken cancellationToken);

    Task<SetupStepResult> LinkBillingAsync(string projectId, string billingAccountId, CancellationToken cancellationToken);

    Task<SetupStepResult> EnableServicesAsync(string projectId, CancellationToken cancellationToken);

    Task<SetupStepResult> EnsureStorageAsync(string projectId, CancellationToken cancellationToken);

    Task<SetupStepResult> CheckQuotaAsync(string projectId, CancellationToken cancellationToken);
}

/// <summary>
/// Issue #99: steps 3 to 7 of the first-run wizard as plain calls that never throw a Google failure at the caller. A
/// <see cref="CloudOperationException"/> becomes a <see cref="SetupStepResult"/> with a code (its message) and one action (its button);
/// cancellation still propagates. Everything that talks to Google goes through the interfaces in <c>Core/Cloud</c> (Hard Rule 7).
/// </summary>
public sealed class SetupCheckService : ISetupCheckService
{
    /// <summary>The GPU kinds the quota step reports, in the order the app prefers them (L4 first, A100 as the fallback tier).</summary>
    private static readonly (string TierKey, string Metric)[] Tiers =
    [
        ("L4", QuotaMetrics.NvidiaL4Gpus),
        ("A100", QuotaMetrics.NvidiaA100Gpus),
    ];

    private readonly IProjectCatalogGateway _projects;
    private readonly IBillingGateway _billingGateway;
    private readonly BillingSetup _billing;
    private readonly IServiceEnablementGateway _services;
    private readonly IStorageGateway _storage;
    private readonly IWorkerIdentityGateway _identity;
    private readonly IQuotaGateway _quota;

    public SetupCheckService(
        IProjectCatalogGateway projects,
        IBillingGateway billingGateway,
        BillingSetup billing,
        IServiceEnablementGateway services,
        IStorageGateway storage,
        IWorkerIdentityGateway identity,
        IQuotaGateway quota)
    {
        _projects = projects;
        _billingGateway = billingGateway;
        _billing = billing;
        _services = services;
        _storage = storage;
        _identity = identity;
        _quota = quota;
    }

    public async Task<SetupStepResult> ListProjectsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var projects = await _projects.ListActiveProjectsAsync(cancellationToken).ConfigureAwait(false);
            return new SetupStepResult(SetupStep.Project, SetupStatus.Ok) { Projects = projects };
        }
        catch (CloudOperationException failure)
        {
            return Failed(SetupStep.Project, SetupErrorMapper.CodeFor(failure), null);
        }
    }

    public async Task<SetupStepResult> CreateProjectAsync(string projectId, string installationId, CancellationToken cancellationToken)
    {
        try
        {
            var created = await _projects.CreateProjectAsync(projectId, ProjectIdGenerator.DisplayName, installationId, cancellationToken).ConfigureAwait(false);
            return new SetupStepResult(SetupStep.Project, SetupStatus.Ok) { ProjectId = created.ProjectId };
        }
        catch (CloudOperationException failure)
        {
            return Failed(SetupStep.Project, SetupErrorMapper.CodeFor(failure), projectId);
        }
    }

    public async Task<SetupStepResult> CheckProjectAsync(string projectId, CancellationToken cancellationToken)
    {
        try
        {
            var project = await _projects.GetProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
            return project is { State: ProjectLifecycleState.Active }
                ? new SetupStepResult(SetupStep.Project, SetupStatus.Ok) { ProjectId = projectId }
                : Failed(SetupStep.Project, AuthErrorCodes.ProjectInvalid, projectId);
        }
        catch (CloudOperationException failure)
        {
            return Failed(SetupStep.Project, SetupErrorMapper.CodeFor(failure), projectId);
        }
    }

    public async Task<SetupStepResult> CheckBillingAsync(string projectId, CancellationToken cancellationToken)
    {
        try
        {
            return FromBilling(projectId, await _billing.EnsureAsync(projectId, cancellationToken).ConfigureAwait(false));
        }
        catch (CloudOperationException failure)
        {
            // BillingSetup links the one open account by itself, so the refusal does not say which account it was. The request text
            // for the billing administrator must name it (the #51 review), so read the list again; a failed read leaves it blank.
            var account = failure.Error.Code == SetupErrorCodes.BillingNoPermission ? await OnlyAccountAsync(cancellationToken).ConfigureAwait(false) : null;
            return Failed(SetupStep.Billing, SetupErrorMapper.CodeFor(failure), projectId) with { AccountId = account };
        }
    }

    public async Task<SetupStepResult> LinkBillingAsync(string projectId, string billingAccountId, CancellationToken cancellationToken)
    {
        try
        {
            return FromBilling(projectId, await _billing.LinkAsync(projectId, billingAccountId, cancellationToken).ConfigureAwait(false));
        }
        catch (CloudOperationException failure)
        {
            return Failed(SetupStep.Billing, SetupErrorMapper.CodeFor(failure), projectId) with { AccountId = billingAccountId };
        }
    }

    public async Task<SetupStepResult> EnableServicesAsync(string projectId, CancellationToken cancellationToken)
    {
        try
        {
            await _services.EnableServicesAsync(projectId, RequiredServices.Ids, cancellationToken).ConfigureAwait(false);
            return new SetupStepResult(SetupStep.Services, SetupStatus.Ok) { ProjectId = projectId };
        }
        catch (CloudOperationException failure)
        {
            return Failed(SetupStep.Services, SetupErrorMapper.CodeFor(failure), projectId);
        }
    }

    public async Task<SetupStepResult> EnsureStorageAsync(string projectId, CancellationToken cancellationToken)
    {
        try
        {
            var bucket = await _storage.EnsureBucketAsync(projectId, cancellationToken).ConfigureAwait(false);
            var identity = await _identity.EnsureWorkerIdentityAsync(projectId, bucket, cancellationToken).ConfigureAwait(false);
            return new SetupStepResult(SetupStep.Storage, SetupStatus.Ok) { ProjectId = projectId, Bucket = bucket, NoteCode = identity.NoteCode };
        }
        catch (CloudOperationException failure)
        {
            return Failed(SetupStep.Storage, SetupErrorMapper.CodeFor(failure), projectId);
        }
    }

    public async Task<SetupStepResult> CheckQuotaAsync(string projectId, CancellationToken cancellationToken)
    {
        QuotaReadResult read;
        try
        {
            read = await _quota.ReadQuotaAsync(projectId, cancellationToken).ConfigureAwait(false);
        }
        catch (CloudOperationException failure)
        {
            return ReadFailed(projectId, SetupErrorMapper.CodeFor(failure));
        }

        var tiers = Tiers.Select(t => TierOf(read, t.TierKey, t.Metric)).ToList();
        if (tiers.Any(t => t.State == QuotaState.Available))
        {
            return new SetupStepResult(SetupStep.Quota, SetupStatus.Ok) { ProjectId = projectId, Tiers = tiers, Link = QuotaLinks.ForProject(projectId), CanSkip = true };
        }

        // A read that failed is never a zero: it keeps the failure's own code (permission and so on).
        if (read.Failures.Count > 0)
        {
            return ReadFailed(projectId, SetupErrorMapper.CodeFor(read.Failures[0].Kind)) with { Tiers = tiers };
        }

        // Nothing failed but nothing could be told either (an unread cap beside a regional allowance): try again, never "zero".
        if (tiers.All(t => t.State == QuotaState.Unknown))
        {
            return ReadFailed(projectId, null) with { Tiers = tiers };
        }

        return new SetupStepResult(SetupStep.Quota, SetupStatus.NeedsAction)
        {
            ProjectId = projectId,
            Tiers = tiers,
            Link = QuotaLinks.ForProject(projectId),
            Action = new SetupAction(SetupActionKind.OpenLink, "SetupAction_RequestGpuAllowance"),
            CanSkip = true,
        };
    }

    /// <summary>
    /// A quota read that could not answer. Permission, org policy, API off and billing abort with their own named action and cannot be skipped
    /// (a run could not start either); any other failure has no action of its own, so it can be retried or skipped for now.
    /// </summary>
    private static SetupStepResult ReadFailed(string projectId, string? code)
        => Failed(SetupStep.Quota, code, projectId) with { CanSkip = code is null, Tiers = Unknown() };

    private static QuotaTierResult TierOf(QuotaReadResult read, string tierKey, string metric)
    {
        if (read.Regions is null && read.AllRegionsGpus is null)
        {
            return new QuotaTierResult(tierKey, metric, 0, QuotaState.Unknown);
        }

        var allowed = read.Regions?.Keys.Count(region => read.GpuRemaining(region, metric) is >= 1) ?? 0;
        var anyUnread = read.Regions is null || read.Regions.Keys.Any(region => read.GpuRemaining(region, metric) is null);
        var state = allowed > 0 ? QuotaState.Available : anyUnread ? QuotaState.Unknown : QuotaState.None;
        return new QuotaTierResult(tierKey, metric, allowed, state);
    }

    private static IReadOnlyList<QuotaTierResult> Unknown() => Tiers.Select(t => new QuotaTierResult(t.TierKey, t.Metric, 0, QuotaState.Unknown)).ToList();

    private static SetupStepResult FromBilling(string projectId, BillingOutcome outcome)
    {
        switch (outcome.Kind)
        {
            case BillingOutcomeKind.AlreadyEnabled:
            case BillingOutcomeKind.Linked:
                return new SetupStepResult(SetupStep.Billing, SetupStatus.Ok) { ProjectId = projectId, AccountId = outcome.AccountId };
            case BillingOutcomeKind.ChooseAccount:
                return new SetupStepResult(SetupStep.Billing, SetupStatus.NeedsAction)
                {
                    ProjectId = projectId,
                    Accounts = outcome.Accounts,
                    Action = new SetupAction(SetupActionKind.PickBillingAccount, "SetupAction_PickAnotherBillingAccount"),
                };
            default:
                return Failed(SetupStep.Billing, outcome.Code, projectId) with { Link = outcome.DeepLink, Accounts = outcome.Accounts, AccountId = outcome.AccountId };
        }
    }

    private async Task<string?> OnlyAccountAsync(CancellationToken cancellationToken)
    {
        try
        {
            var accounts = await _billingGateway.ListOpenBillingAccountsAsync(cancellationToken).ConfigureAwait(false);
            return accounts.Count > 0 ? accounts[0].AccountId : null;
        }
        catch (CloudOperationException)
        {
            return null;
        }
    }

    private static SetupStepResult Failed(SetupStep step, string? code, string? projectId)
    {
        var kind = SetupActions.KindFor(code);
        return new SetupStepResult(step, SetupStatus.NeedsAction)
        {
            ProjectId = projectId,
            Code = code,
            Action = new SetupAction(kind, SetupActions.ResourceKeyFor(code)),
            Link = kind == SetupActionKind.OpenLink && projectId is not null ? LinkFor(code, projectId) : null,
        };
    }

    private static string? LinkFor(string? code, string projectId) => code switch
    {
        SetupErrorCodes.NoBilling or SetupErrorCodes.BillingAccountOff => BillingLinks.ForProject(projectId),
        SetupErrorCodes.WorkerDefaultAccountMissing => ServiceAccountLinks.ForProject(projectId),
        _ => null,
    };
}
