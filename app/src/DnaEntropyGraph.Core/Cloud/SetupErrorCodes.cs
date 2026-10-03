namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// The codes the project-setup wizard steps fail under (issues #50 to #53), carried in <see cref="CloudError.Code"/>
/// of a <see cref="CloudOperationException"/>. Same pattern as <see cref="AuthErrorCodes"/>: the English lives in
/// <c>Resources.resw</c> under <see cref="ResourceKey"/>, names one action (Hard Rule 13), and the raw Google text is
/// kept only for the diagnostics zip. <c>docs/copy_catalog.md</c> and <c>scripts/triage_diagnostics.py</c> list the
/// same codes; <c>Guards.Tests/SetupErrorResourceTests</c> fails when any of the three drifts from <see cref="All"/>.
/// </summary>
public static class SetupErrorCodes
{
    /// <summary>Google refused to create another project: the account has hit its project limit. Action: pick an existing project.</summary>
    public const string ProjectQuota = "PROJECT_QUOTA";

    /// <summary>An organization policy refused the request. Action: copy the message for the IT administrator.</summary>
    public const string OrgPolicyBlock = "ORG_POLICY_BLOCK";

    /// <summary>The account is not allowed to do this in the project. Action: copy a request for the project owner.</summary>
    public const string Permission = "PERMISSION";

    /// <summary>A Google service the step needs is switched off in the project. Action: turn it on.</summary>
    public const string ApiDisabled = "API_DISABLED";

    /// <summary>The project has no billing account and the user has none to link (<see cref="BillingOutcomeKind.NeedsAccount"/>). Action: open Google's billing page (a link).</summary>
    public const string NoBilling = "NO_BILLING";

    /// <summary>The user can use a billing account but is not allowed to link projects to it. Action: copy the request for the billing admin.</summary>
    public const string BillingNoPermission = "BILLING_NO_PERMISSION";

    /// <summary>The user is a member of the project but not its Owner, so Google refused to switch a service on (HTTP 403). Action: create a project of their own.</summary>
    public const string NotProjectOwner = "NOT_PROJECT_OWNER";

    /// <summary>Google accepted the billing account but the project still has billing off (a suspended account, no valid payment method). Action: pick another billing account.</summary>
    public const string BillingStillOff = "BILLING_STILL_OFF";

    /// <summary>The billing account the project is linked to is not working and no other open account exists to pick (<see cref="BillingOutcomeKind.FixLinkedAccount"/>). Action: open the project's billing page on Google and fix or replace the account.</summary>
    public const string BillingAccountOff = "BILLING_ACCOUNT_OFF";

    /// <summary>Google accepted the results bucket but reading it back shows UBLA, public access prevention or a lifecycle rule is not what was asked for ("applied is not present"). Action: try again (the next attempt repairs the bucket).</summary>
    public const string BucketConfigNotApplied = "BUCKET_CONFIG_NOT_APPLIED";

    /// <summary>Every results-bucket name the app tried was taken by someone else. Action: try again (new names are drawn).</summary>
    public const string BucketNameTaken = "BUCKET_NAME_TAKEN";

    /// <summary>
    /// An organization policy refuses service-account creation, so the VM will run as the project's default Compute Engine
    /// account instead (issue #54). Not a failure: the wizard shows it as a yellow note. Action: Continue.
    /// </summary>
    public const string WorkerDefaultAccount = "WORKER_DEFAULT_ACCOUNT";

    /// <summary>Google accepted the worker role or its bindings but reading them back shows something is not as asked ("applied is not present"). Action: try again (the next attempt repairs it).</summary>
    public const string WorkerIdentityNotApplied = "WORKER_IDENTITY_NOT_APPLIED";

    /// <summary>The user may not run a VM as the worker identity (<c>iam.serviceAccounts.actAs</c> missing, Compute Admin alone lacks it). Action: copy a request for the project owner.</summary>
    public const string PermissionActAs = "PERMISSION_ACTAS";

    /// <summary>Every code a setup step can fail under.</summary>
    public static IReadOnlyList<string> All { get; } = [ProjectQuota, OrgPolicyBlock, Permission, ApiDisabled, NoBilling, BillingNoPermission, BillingStillOff, NotProjectOwner, BillingAccountOff, BucketConfigNotApplied, BucketNameTaken, WorkerDefaultAccount, WorkerIdentityNotApplied, PermissionActAs];

    /// <summary>The <c>Resources.resw</c> key for a code's message (a literal per code so the orphan-resource scan sees it).</summary>
    public static string ResourceKey(string? code) => code switch
    {
        ProjectQuota => "SetupError_PROJECT_QUOTA",
        OrgPolicyBlock => "SetupError_ORG_POLICY_BLOCK",
        Permission => "SetupError_PERMISSION",
        ApiDisabled => "SetupError_API_DISABLED",
        NoBilling => "SetupError_NO_BILLING",
        BillingNoPermission => "SetupError_BILLING_NO_PERMISSION",
        NotProjectOwner => "SetupError_NOT_PROJECT_OWNER",
        BillingStillOff => "SetupError_BILLING_STILL_OFF",
        BillingAccountOff => "SetupError_BILLING_ACCOUNT_OFF",
        BucketConfigNotApplied => "SetupError_BUCKET_CONFIG_NOT_APPLIED",
        BucketNameTaken => "SetupError_BUCKET_NAME_TAKEN",
        WorkerDefaultAccount => "SetupError_WORKER_DEFAULT_ACCOUNT",
        WorkerIdentityNotApplied => "SetupError_WORKER_IDENTITY_NOT_APPLIED",
        PermissionActAs => "SetupError_PERMISSION_ACTAS",
        _ => "SetupError_OTHER",
    };

    /// <summary>The <c>Resources.resw</c> key for the button that carries the one action. Every message names one, the catch-all included.</summary>
    public static string ActionResourceKey(string? code) => code switch
    {
        ProjectQuota => "SetupAction_PickExistingProject",
        OrgPolicyBlock => "SetupAction_CopyMessageForIt",
        Permission => "SetupAction_CopyRequestForOwner",
        ApiDisabled => "SetupAction_TurnItOn",
        NoBilling => "SetupAction_LinkBilling",
        BillingNoPermission => "SetupAction_CopyBillingRequest",
        NotProjectOwner => "SetupAction_CreateProject",
        BillingStillOff => "SetupAction_PickAnotherBillingAccount",
        BillingAccountOff => "SetupAction_FixBillingAccount",
        BucketConfigNotApplied => "SetupAction_TryAgain",
        BucketNameTaken => "SetupAction_TryAgain",
        WorkerDefaultAccount => "SetupAction_Continue",
        WorkerIdentityNotApplied => "SetupAction_TryAgain",
        PermissionActAs => "SetupAction_CopyRequestForOwner",
        _ => "SetupAction_TryAgain",
    };
}
