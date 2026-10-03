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

    /// <summary>The project has no billing account and the user has none to link (<see cref="BillingOutcomeKind.NeedsAccount"/>). Action: open Google's billing page (a link).</summary>
    public const string NoBilling = "NO_BILLING";

    /// <summary>The user can use a billing account but is not allowed to link projects to it. Action: copy the request for the billing admin.</summary>
    public const string BillingNoPermission = "BILLING_NO_PERMISSION";

    /// <summary>Every code a setup step can fail under.</summary>
    public static IReadOnlyList<string> All { get; } = [ProjectQuota, OrgPolicyBlock, NoBilling, BillingNoPermission];

    /// <summary>The <c>Resources.resw</c> key for a code's message (a literal per code so the orphan-resource scan sees it).</summary>
    public static string ResourceKey(string? code) => code switch
    {
        ProjectQuota => "SetupError_PROJECT_QUOTA",
        OrgPolicyBlock => "SetupError_ORG_POLICY_BLOCK",
        NoBilling => "SetupError_NO_BILLING",
        BillingNoPermission => "SetupError_BILLING_NO_PERMISSION",
        _ => "SetupError_OTHER",
    };

    /// <summary>The <c>Resources.resw</c> key for the button that carries the one action, or null when the message itself says it.</summary>
    public static string? ActionResourceKey(string? code) => code switch
    {
        ProjectQuota => "SetupAction_PickExistingProject",
        OrgPolicyBlock => "SetupAction_CopyMessageForIt",
        NoBilling => "SetupAction_LinkBilling",
        BillingNoPermission => "SetupAction_CopyBillingRequest",
        _ => null,
    };
}
