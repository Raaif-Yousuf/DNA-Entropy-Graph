namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// The codes a failed run is recorded under (<c>RunRecord.ErrorCode</c>) and
/// the resource key whose text the UI shows for each. Hard Rule 13: nothing a
/// user can see is built in C#. A failure carries a code; the English lives in
/// <c>Resources.resw</c> under <see cref="ResourceKey"/>, names one action, and
/// the raw exception or API text is kept only in <c>RunRecord.ErrorDetail</c>
/// for the diagnostics zip.
/// </summary>
public static class RunErrorCodes
{
    public const string NoProject = "no_project";
    public const string TargetNotSupported = "target_not_supported";
    public const string VmUnhealthy = "vm_unhealthy";
    public const string LifecycleUnverified = "lifecycle_unverified";
    public const string CancelFailed = "cancel_failed";
    public const string Other = "other";

    /// <summary>Every code a run can be recorded under.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        NoProject, TargetNotSupported, VmUnhealthy, LifecycleUnverified, CancelFailed,
        "billing", "api_disabled", "quota", "stockout", "already_exists", "permission", "org_policy", "network", Other,
    ];

    /// <summary>The <c>CloudErrorKind</c> wire names from docs/cloud_design.md section 5.</summary>
    public static string For(CloudErrorKind kind) => kind switch
    {
        CloudErrorKind.Billing => "billing",
        CloudErrorKind.ApiDisabled => "api_disabled",
        CloudErrorKind.Quota => "quota",
        CloudErrorKind.Stockout => "stockout",
        CloudErrorKind.AlreadyExists => "already_exists",
        CloudErrorKind.Permission => "permission",
        CloudErrorKind.OrgPolicy => "org_policy",
        CloudErrorKind.Network => "network",
        _ => Other,
    };

    /// <summary>The <c>Resources.resw</c> key for a code (plain, non-dotted: it is read from code, not through <c>x:Uid</c>). An unknown code gets the generic message.</summary>
    public static string ResourceKey(string? code) => code switch
    {
        NoProject => "RunError_no_project",
        TargetNotSupported => "RunError_target_not_supported",
        VmUnhealthy => "RunError_vm_unhealthy",
        LifecycleUnverified => "RunError_lifecycle_unverified",
        CancelFailed => "RunError_cancel_failed",
        "billing" => "RunError_billing",
        "api_disabled" => "RunError_api_disabled",
        "quota" => "RunError_quota",
        "stockout" => "RunError_stockout",
        "already_exists" => "RunError_already_exists",
        "permission" => "RunError_permission",
        "org_policy" => "RunError_org_policy",
        "network" => "RunError_network",
        _ => "RunError_other",
    };
}
