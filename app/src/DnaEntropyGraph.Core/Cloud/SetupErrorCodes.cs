namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// The codes the project-setup wizard steps fail under (issues #50 to #52), carried in <see cref="CloudError.Code"/>
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

    /// <summary>Every code a setup step can fail under.</summary>
    public static IReadOnlyList<string> All { get; } = [ProjectQuota, OrgPolicyBlock, Permission, ApiDisabled, NoBilling, BillingNoPermission, BillingStillOff, NotProjectOwner, BillingAccountOff];

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
        _ => "SetupAction_TryAgain",
    };
}
