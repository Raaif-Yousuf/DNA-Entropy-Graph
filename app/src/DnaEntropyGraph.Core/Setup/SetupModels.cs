using DnaEntropyGraph.Core.Cloud;

namespace DnaEntropyGraph.Core.Setup;

/// <summary>The cloud setup steps of the first-run wizard that talk to Google (docs/superpowers/specs Appendix A section 2.1, steps 3 to 7). Sign in, welcome and the test run are not cloud checks.</summary>
public enum SetupStep
{
    Project,
    Billing,
    Services,
    Storage,
    Quota,
}

public enum SetupStatus
{
    /// <summary>Not looked at yet.</summary>
    Pending,

    /// <summary>A call to Google is in flight.</summary>
    Running,

    /// <summary>Green: nothing for the user to do.</summary>
    Ok,

    /// <summary>Red (the "Fix" row): <see cref="SetupStepResult.Code"/> says why and <see cref="SetupStepResult.Action"/> is the one thing to do.</summary>
    NeedsAction,

    /// <summary>The user chose to come back to this step later (only offered where <see cref="SetupStepResult.CanSkip"/>).</summary>
    Skipped,
}

/// <summary>What the one button of a failed step does. The wizard page binds each kind to a different command; there is exactly one per message (Hard Rule 13).</summary>
public enum SetupActionKind
{
    None,

    /// <summary>Run the step again (the "Check again" / "Try again" / "Turn it on" buttons).</summary>
    Retry,

    /// <summary>Open <see cref="SetupStepResult.Link"/> in the browser.</summary>
    OpenLink,

    /// <summary>Put a ready-to-send message on the clipboard (for an administrator or the project owner).</summary>
    CopyText,

    /// <summary>Choose a different existing project.</summary>
    PickProject,

    /// <summary>Choose one of <see cref="SetupStepResult.Accounts"/>.</summary>
    PickBillingAccount,

    /// <summary>Make a new project of the user's own.</summary>
    CreateProject,

    /// <summary>Carry on: the message was a note, not a failure.</summary>
    Continue,
}

/// <summary>The one action of a step: its kind and the <c>Resources.resw</c> key of its button label.</summary>
public sealed record SetupAction(SetupActionKind Kind, string? ResourceKey)
{
    public static SetupAction None { get; } = new(SetupActionKind.None, null);
}

/// <summary>Which GPU kind and how many regions allow it (step 7's card per tier).</summary>
public sealed record QuotaTierResult(string TierKey, string Metric, int AllowedRegions, QuotaState State);

/// <summary>
/// What one step found, as plain data a ViewModel can show. Only the fields a step fills are set: the project list after listing,
/// <see cref="AccountId"/> and <see cref="Accounts"/> after billing, <see cref="Bucket"/> after storage, <see cref="Tiers"/> after quota.
/// </summary>
public sealed record SetupStepResult(SetupStep Step, SetupStatus Status)
{
    /// <summary>A <see cref="SetupErrorCodes"/> or <see cref="AuthErrorCodes"/> code; null when nothing is wrong or the catch-all applies (<see cref="Failed"/>).</summary>
    public string? Code { get; init; }

    public SetupAction Action { get; init; } = SetupAction.None;

    /// <summary>The page <see cref="SetupActionKind.OpenLink"/> opens.</summary>
    public string? Link { get; init; }

    public string? ProjectId { get; init; }

    public IReadOnlyList<ProjectSummary> Projects { get; init; } = [];

    /// <summary>The billing account in play: the one billing is on for, or the one a link was refused for (it fills the request text for the billing administrator).</summary>
    public string? AccountId { get; init; }

    public IReadOnlyList<BillingAccountSummary> Accounts { get; init; } = [];

    public string? Bucket { get; init; }

    /// <summary>A yellow note on an OK step (<see cref="SetupErrorCodes.WorkerDefaultAccount"/>).</summary>
    public string? NoteCode { get; init; }

    public IReadOnlyList<QuotaTierResult> Tiers { get; init; } = [];

    /// <summary>True where the user may carry on without this step (GPU allowance: the pipeline test still works).</summary>
    public bool CanSkip { get; init; }

    public bool IsOk => Status == SetupStatus.Ok;
}

/// <summary>The Cloud console's quota page, pre-filtered to the project (a link action, Hard Rule 13).</summary>
public static class QuotaLinks
{
    public static string ForProject(string projectId)
        => "https://console.cloud.google.com/iam-admin/quotas?project=" + Uri.EscapeDataString(projectId);
}

/// <summary>Turns a Google failure into the setup code whose message and button the wizard shows (null means the catch-all "something went wrong, try again").</summary>
public static class SetupErrorMapper
{
    public static string? CodeFor(CloudOperationException failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (failure.Error.Code is { } code && SetupErrorCodes.All.Contains(code))
        {
            return code;
        }

        return CodeFor(failure.Kind);
    }

    public static string? CodeFor(CloudErrorKind kind) => kind switch
    {
        CloudErrorKind.Permission => SetupErrorCodes.Permission,
        CloudErrorKind.OrgPolicy => SetupErrorCodes.OrgPolicyBlock,
        CloudErrorKind.ApiDisabled => SetupErrorCodes.ApiDisabled,
        CloudErrorKind.Billing => SetupErrorCodes.NoBilling,
        _ => null,
    };
}

/// <summary>Which kind of button each setup message carries (the label is <see cref="SetupErrorCodes.ActionResourceKey"/>).</summary>
public static class SetupActions
{
    public static SetupActionKind KindFor(string? code) => code switch
    {
        SetupErrorCodes.ProjectQuota => SetupActionKind.PickProject,
        AuthErrorCodes.ProjectInvalid => SetupActionKind.PickProject,
        SetupErrorCodes.OrgPolicyBlock => SetupActionKind.CopyText,
        SetupErrorCodes.Permission => SetupActionKind.CopyText,
        SetupErrorCodes.PermissionActAs => SetupActionKind.CopyText,
        SetupErrorCodes.ApiDisabled => SetupActionKind.Retry,
        SetupErrorCodes.NoBilling => SetupActionKind.OpenLink,
        SetupErrorCodes.BillingNoPermission => SetupActionKind.CopyText,
        SetupErrorCodes.NotProjectOwner => SetupActionKind.CreateProject,
        SetupErrorCodes.BillingStillOff => SetupActionKind.PickBillingAccount,
        SetupErrorCodes.BillingAccountOff => SetupActionKind.OpenLink,
        SetupErrorCodes.BucketConfigNotApplied => SetupActionKind.Retry,
        SetupErrorCodes.BucketNameTaken => SetupActionKind.Retry,
        SetupErrorCodes.WorkerDefaultAccount => SetupActionKind.Continue,
        SetupErrorCodes.WorkerIdentityNotApplied => SetupActionKind.Retry,
        SetupErrorCodes.WorkerDefaultAccountMissing => SetupActionKind.OpenLink,
        _ => SetupActionKind.Retry,
    };

    /// <summary>The button label key. A project the account cannot use offers "pick another project"; the catch-all is "Try again".</summary>
    public static string ResourceKeyFor(string? code)
        => code == AuthErrorCodes.ProjectInvalid ? "SetupAction_PickExistingProject" : SetupErrorCodes.ActionResourceKey(code);
}
