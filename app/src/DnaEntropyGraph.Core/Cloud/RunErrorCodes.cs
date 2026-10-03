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
    public const string InputMissing = "input_missing";
    public const string WorkerImageUnavailable = "no_worker_image";
    public const string WorkerImageRefused = "worker_image_refused";
    public const string ResultTimeout = "result_timeout";
    public const string WorkerNoHeartbeat = "worker_no_heartbeat";
    public const string WorkerHeartbeatStale = "worker_heartbeat_stale";
    public const string WorkerFailed = "worker_failed";
    public const string DownloadFailed = "download_failed";
    public const string DownloadCorrupt = "download_corrupt";
    public const string VmEndUnconfirmed = "vm_end_unconfirmed";
    public const string GpuNotVisible = "gpu_not_visible";
    public const string ImagePullFailed = "image_pull_failed";
    public const string ManifestInvalid = "manifest_invalid";
    public const string WorkerCrashed = "worker_crashed";
    public const string CloudNotConnected = "cloud_not_connected";
    public const string ModelNeedsHopper = "model_needs_hopper";
    public const string ModelOom = "model_oom";
    public const string BatchLimitExceeded = "batch_limit_exceeded";
    public const string InputInvalid = "input_invalid";
    public const string WorkerVersionMismatch = "worker_version_mismatch";
    public const string OutputFolderUnusable = "output_folder_unusable";
    public const string InputInvalidCharacter = "input_invalid_character";
    public const string InputAmbiguityRefused = "input_ambiguity_refused";
    public const string InputRna = "input_rna";
    public const string InputNotUtf8 = "input_not_utf8";
    public const string InputEmpty = "input_empty";
    public const string InputTooLong = "input_too_long";

    /// <summary>The installation id file was unusable and set aside (#558). Minimal copy only; the recovery UX is owner-only DECISION #404.</summary>
    public const string InstallationIdUnusable = "installation_id_unusable";

    /// <summary>The <c>CloudError.Code</c> a gateway with no real cloud behind it throws; the runner records it as <see cref="CloudNotConnected"/>.</summary>
    public const string NotConnectedGatewayCode = "CLOUD_NOT_CONNECTED";
    public const string Other = "other";

    /// <summary>Every code a run can be recorded under.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        NoProject, TargetNotSupported, VmUnhealthy, LifecycleUnverified, CancelFailed,
        InputMissing, WorkerImageUnavailable, WorkerImageRefused, ResultTimeout, WorkerNoHeartbeat, WorkerHeartbeatStale, VmEndUnconfirmed, WorkerFailed, DownloadFailed, DownloadCorrupt,
        GpuNotVisible, ImagePullFailed, ManifestInvalid, WorkerCrashed, CloudNotConnected,
        ModelNeedsHopper, ModelOom, BatchLimitExceeded, InputInvalid, WorkerVersionMismatch, OutputFolderUnusable,
        InputInvalidCharacter, InputAmbiguityRefused, InputRna, InputNotUtf8, InputEmpty, InputTooLong, InstallationIdUnusable,
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

    /// <summary>
    /// The code for an <c>error.code</c> the VM's startup script or the worker wrote, in <c>status.json</c> when no
    /// <c>result.json</c> followed (docs/job_contract.md section 4) or in <c>result.json</c> itself. One per worker code
    /// that docs/copy_catalog.md gives its own user action. Null for a code with no dedicated copy.
    /// </summary>
    public static string? ForWorkerStatusCode(string? workerCode) => workerCode switch
    {
        "GPU_NOT_VISIBLE" => GpuNotVisible,
        "IMAGE_PULL_FAILED" => ImagePullFailed,
        "MANIFEST_INVALID" => ManifestInvalid,
        "WORKER_CRASH" => WorkerCrashed,
        "MODEL_NEEDS_HOPPER" => ModelNeedsHopper,
        "MODEL_OOM" => ModelOom,
        "BATCH_LIMIT_EXCEEDED" => BatchLimitExceeded,
        "INPUT_INVALID" => InputInvalid,
        "WORKER_VERSION_MISMATCH" => WorkerVersionMismatch,
        _ => null,
    };

    /// <summary>
    /// Worker error codes (docs/contract/error-codes.json) deliberately left to the generic <see cref="WorkerFailed"/> copy,
    /// because docs/copy_catalog.md has no row, and so no user action, for them. A guard test fails when a code in the
    /// contract is neither mapped by <see cref="ForWorkerStatusCode"/> nor listed here, and when an entry here goes stale.
    /// </summary>
    public static IReadOnlyList<string> WorkerStatusCodesWithoutDedicatedCopy { get; } = ["MODEL_UNKNOWN"];

    /// <summary>The <c>Resources.resw</c> key for a code (plain, non-dotted: it is read from code, not through <c>x:Uid</c>). An unknown code gets the generic message.</summary>
    public static string ResourceKey(string? code) => code switch
    {
        NoProject => "RunError_no_project",
        TargetNotSupported => "RunError_target_not_supported",
        VmUnhealthy => "RunError_vm_unhealthy",
        LifecycleUnverified => "RunError_lifecycle_unverified",
        CancelFailed => "RunError_cancel_failed",
        InputMissing => "RunError_input_missing",
        WorkerImageUnavailable => "RunError_no_worker_image",
        WorkerImageRefused => "RunError_worker_image_refused",
        ResultTimeout => "RunError_result_timeout",
        WorkerNoHeartbeat => "RunError_worker_no_heartbeat",
        WorkerHeartbeatStale => "RunError_worker_heartbeat_stale",
        WorkerFailed => "RunError_worker_failed",
        DownloadFailed => "RunError_download_failed",
        DownloadCorrupt => "RunError_download_corrupt",
        VmEndUnconfirmed => "RunError_vm_end_unconfirmed",
        GpuNotVisible => "RunError_gpu_not_visible",
        ImagePullFailed => "RunError_image_pull_failed",
        ManifestInvalid => "RunError_manifest_invalid",
        WorkerCrashed => "RunError_worker_crashed",
        CloudNotConnected => "RunError_cloud_not_connected",
        ModelNeedsHopper => "RunError_model_needs_hopper",
        ModelOom => "RunError_model_oom",
        BatchLimitExceeded => "RunError_batch_limit_exceeded",
        InputInvalid => "RunError_input_invalid",
        WorkerVersionMismatch => "RunError_worker_version_mismatch",
        OutputFolderUnusable => "RunError_output_folder_unusable",
        InputInvalidCharacter => "RunError_input_invalid_character",
        InputAmbiguityRefused => "RunError_input_ambiguity_refused",
        InputRna => "RunError_input_rna",
        InputNotUtf8 => "RunError_input_not_utf8",
        InputEmpty => "RunError_input_empty",
        InputTooLong => "RunError_input_too_long",
        InstallationIdUnusable => "RunError_installation_id_unusable",
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
