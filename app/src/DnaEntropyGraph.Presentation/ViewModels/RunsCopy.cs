using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Runs;

namespace DnaEntropyGraph.Presentation.ViewModels;

/// <summary>One toast: its two resw keys and how it is shown (an error stays until closed, issue #585).</summary>
public readonly record struct RunsMessage(string Title, string Body, ToastSeverity Severity);

/// <summary>
/// Every <c>Resources.resw</c> key the Runs page's ViewModel reads (Hard Rule 13). Plain, non-dotted keys,
/// because they are read from code. <see cref="AllKeys"/> is what the guard checks against the .resw.
/// </summary>
public static class RunsCopy
{
    public const string EmptyNone = "Runs_Empty_None";
    public const string EmptyNoMatch = "Runs_Empty_NoMatch";
    public const string GroupToday = "Runs_Group_Today";
    public const string GroupYesterday = "Runs_Group_Yesterday";
    public const string StatusActive = "Runs_Status_Active";
    public const string StatusCompleted = "Runs_Status_Completed";
    public const string StatusPartial = "Runs_Status_Partial";
    public const string StatusFailed = "Runs_Status_Failed";
    public const string StatusCancelled = "Runs_Status_Cancelled";
    public const string DeleteCloudConfirmTitle = "Runs_DeleteCloud_Confirm_Title";
    public const string DeleteCloudConfirmBody = "Runs_DeleteCloud_Confirm_Body";
    public const string DeleteLocalConfirmTitle = "Runs_DeleteLocal_Confirm_Title";
    public const string DeleteLocalConfirmBody = "Runs_DeleteLocal_Confirm_Body";
    /// <summary>Issue #934: the one question for a folder with no marker (made before markers existed, or the user's own); <c>{0}</c> in the body is the folder's full path.</summary>
    public const string DeleteLocalUnmarkedConfirmTitle = "Runs_DeleteLocal_Unmarked_Confirm_Title";
    public const string DeleteLocalUnmarkedConfirmBody = "Runs_DeleteLocal_Unmarked_Confirm_Body";

    /// <summary>Issue #934, #954: the button that opens the run's folder in File Explorer from a refusal, and the button that just closes it.</summary>
    public const string OpenFolderLabel = "Runs_OpenFolder_Label";
    public const string CloseLabel = "Runs_Close_Label";

    /// <summary>Issue #954: the tooltips on Delete files on this PC and Run again's re-download for a run saved straight to a drive (<c>D:\</c>): why, and the one action.</summary>
    public const string DeleteLocalHintDriveRoot = "Runs_DeleteLocal_Hint_DriveRoot";
    public const string RedownloadHintDriveRoot = "Runs_Redownload_Hint_DriveRoot";
    public const string DeleteCloudHintRunning = "Runs_DeleteCloud_Hint_Running";
    public const string DeleteCloudHintNoCopy = "Runs_DeleteCloud_Hint_NoCopy";
    public const string DeleteCloudHintNotConnected = "Runs_DeleteCloud_Hint_NotConnected";
    public const string RemoveConfirmTitle = "Runs_Remove_Confirm_Title";
    public const string RemoveConfirmBody = "Runs_Remove_Confirm_Body";

    /// <summary>The tooltip on Delete files on this PC for a run imported from the earlier version (issue #243): its folder is the user's own.</summary>
    public const string DeleteLocalHintImported = "Runs_DeleteLocal_Hint_Imported";

    /// <summary>The request text for the project owner (issue #604). <c>{0}</c> the role, <c>{1}</c> the worker account's email, <c>{2}</c> the project id, <c>{3}</c> the grantee clause.</summary>
    public const string OwnerRequestActAs = "Runs_OwnerRequest_ActAs";

    /// <summary>The clause naming who is to receive the role (<c>{0}</c> the signed-in email), the request text's <c>{3}</c>; left out when nobody is signed in.</summary>
    public const string OwnerRequestGrantee = "Runs_OwnerRequest_Grantee";
    public const string PresetLine = "Runs_PresetLine";
    public const string DeleteCloudHintImported = "Runs_DeleteCloud_Hint_Imported";
    public const string DeleteCloudHintPrototype = "Runs_DeleteCloud_Hint_Prototype";
    /// <summary>Issue #914: a row found in the bucket whose job folder cannot be linked to; the one action left is Remove from list.</summary>
    public const string DeleteCloudHintImportedNoLink = "Runs_DeleteCloud_Hint_ImportedNoLink";
    /// <summary>Issue #914: a prototype row whose folder is gone (Open would only say so); the one action left is Remove from list.</summary>
    public const string DeleteCloudHintPrototypeNoFolder = "Runs_DeleteCloud_Hint_PrototypeNoFolder";
    public const string ImportedBadge = "Runs_Imported_Badge";
    public const string ImportRunning = "Runs_Import_Running";
    public const string ImportNone = "Runs_Import_None";
    public const string ImportDoneOne = "Runs_Import_Done_One";
    public const string ImportDoneMany = "Runs_Import_Done_Many";
    public const string ImportNoProject = "Runs_Import_NoProject";
    public const string ImportFailed = "Runs_Import_Failed";
    public const string ImportFailedPermission = "Runs_Import_Failed_Permission";
    public const string ImportFailedSetup = "Runs_Import_Failed_Setup";
    public const string ImportFailedNetwork = "Runs_Import_Failed_Network";
    public const string ImportFailedSignIn = "Runs_Import_Failed_SignIn";

    public static readonly RunsMessage OwnerRequestCopied = Pair("Runs_OwnerRequest_Copied_Title", "Runs_OwnerRequest_Copied_Body", ToastSeverity.Success);
    public static readonly RunsMessage OwnerRequestCopyFailed = Pair("Runs_OwnerRequest_CopyFailed_Title", "Runs_OwnerRequest_CopyFailed_Body", ToastSeverity.Error);
    public static readonly RunsMessage OpenMissing = Pair("Runs_Open_Missing_Title", "Runs_Open_Missing_Body", ToastSeverity.Warning);
    public static readonly RunsMessage RerunNoOptions = Pair("Runs_Rerun_NoOptions_Title", "Runs_Rerun_NoOptions_Body", ToastSeverity.Warning);
    public static readonly RunsMessage RerunNoInput = Pair("Runs_Rerun_NoInput_Title", "Runs_Rerun_NoInput_Body", ToastSeverity.Warning);
    public static readonly RunsMessage RerunFailed = Pair("Runs_Rerun_Failed_Title", "Runs_Rerun_Failed_Body", ToastSeverity.Error);
    public static readonly RunsMessage RemoveFailed = Pair("Runs_Remove_Failed_Title", "Runs_Remove_Failed_Body", ToastSeverity.Error);
    public static readonly RunsMessage RefreshFailed = Pair("Runs_Refresh_Failed_Title", "Runs_Refresh_Failed_Body", ToastSeverity.Error);

    private static readonly string[] FilterKeys = ["Runs_Filter_All", "Runs_Filter_Active", "Runs_Filter_Completed", "Runs_Filter_Failed", "Runs_Filter_Cancelled"];

    /// <summary>The "changed files kept" copy for <paramref name="count"/> files: singular for exactly one (the <c>_One</c> key), else plural.</summary>
    public static RunsMessage RedownloadChangedKeptAside(int count) => count == 1
        ? Pair("Runs_Redownload_Changed_Title", "Runs_Redownload_Changed_Body_One", ToastSeverity.Info)
        : Pair("Runs_Redownload_Changed_Title", "Runs_Redownload_Changed_Body", ToastSeverity.Info);

    public const string RedownloadAgainLabel = "Runs_RedownloadAgain_Label";
    public const string RedownloadPartialLabel = "Runs_RedownloadPartial_Label";
    public static readonly RunsMessage RedownloadCancelledPartial = Pair("Runs_Redownload_CancelledPartial_Title", "Runs_Redownload_CancelledPartial_Body", ToastSeverity.Info);

    public static RunsMessage Redownload(CloudResultsStatus status) => status switch
    {
        CloudResultsStatus.Done => Pair("Runs_Redownload_Done_Title", "Runs_Redownload_Done_Body", ToastSeverity.Success),
        CloudResultsStatus.Partial => Pair("Runs_Redownload_Partial_Title", "Runs_Redownload_Partial_Body", ToastSeverity.Warning),
        CloudResultsStatus.Expired => Pair("Runs_Redownload_Expired_Title", "Runs_Redownload_Expired_Body", ToastSeverity.Warning),
        CloudResultsStatus.NoCloudCopy => Pair("Runs_Redownload_NoCloudCopy_Title", "Runs_Redownload_NoCloudCopy_Body", ToastSeverity.Info),
        CloudResultsStatus.ResultNotFound => Pair("Runs_Redownload_ResultNotFound_Title", "Runs_Redownload_ResultNotFound_Body", ToastSeverity.Error),
        CloudResultsStatus.Refused => Pair("Runs_Redownload_Refused_Title", "Runs_Redownload_Refused_Body", ToastSeverity.Error),
        CloudResultsStatus.DriveRootRefused => Pair("Runs_Redownload_DriveRoot_Title", "Runs_Redownload_DriveRoot_Body", ToastSeverity.Error),
        _ => Pair("Runs_Redownload_Failed_Title", "Runs_Redownload_Failed_Body", ToastSeverity.Error),
    };

    /// <summary>The cloud-delete copy for a failure the gateway classified: each names its own action. Anything else is the general failure copy.</summary>
    public static RunsMessage DeleteCloudFailed(CloudErrorKind? kind) => kind switch
    {
        CloudErrorKind.Permission => Pair("Runs_DeleteCloud_FailedPermission_Title", "Runs_DeleteCloud_FailedPermission_Body", ToastSeverity.Error),
        CloudErrorKind.OrgPolicy => Pair("Runs_DeleteCloud_FailedOrgPolicy_Title", "Runs_DeleteCloud_FailedOrgPolicy_Body", ToastSeverity.Error),
        CloudErrorKind.Billing => Pair("Runs_DeleteCloud_FailedBilling_Title", "Runs_DeleteCloud_FailedBilling_Body", ToastSeverity.Error),
        CloudErrorKind.ApiDisabled => Pair("Runs_DeleteCloud_FailedApi_Title", "Runs_DeleteCloud_FailedApi_Body", ToastSeverity.Error),
        CloudErrorKind.Quota => Pair("Runs_DeleteCloud_FailedQuota_Title", "Runs_DeleteCloud_FailedQuota_Body", ToastSeverity.Error),
        // The "check your internet connection" copy is for a network failure only.
        CloudErrorKind.Network => Pair("Runs_DeleteCloud_Failed_Title", "Runs_DeleteCloud_Failed_Body", ToastSeverity.Error),
        _ => Pair("Runs_DeleteCloud_FailedOther_Title", "Runs_DeleteCloud_FailedOther_Body", ToastSeverity.Error),
    };

    /// <summary>
    /// A cloud delete that stopped on a classified failure and could not read what is left: some files may have been deleted and some may not, so it never says
    /// "nothing was deleted". Each cause names its own one action, as <see cref="DeleteCloudFailed"/> does.
    /// </summary>
    public static RunsMessage DeleteCloudPartialMaybe(CloudErrorKind kind) => kind switch
    {
        CloudErrorKind.Permission => Pair("Runs_DeleteCloud_PartialMaybe_Title", "Runs_DeleteCloud_PartialMaybe_Body_Permission", ToastSeverity.Warning),
        CloudErrorKind.OrgPolicy => Pair("Runs_DeleteCloud_PartialMaybe_Title", "Runs_DeleteCloud_PartialMaybe_Body_OrgPolicy", ToastSeverity.Warning),
        CloudErrorKind.Billing => Pair("Runs_DeleteCloud_PartialMaybe_Title", "Runs_DeleteCloud_PartialMaybe_Body_Billing", ToastSeverity.Warning),
        CloudErrorKind.ApiDisabled => Pair("Runs_DeleteCloud_PartialMaybe_Title", "Runs_DeleteCloud_PartialMaybe_Body_Api", ToastSeverity.Warning),
        CloudErrorKind.Quota => Pair("Runs_DeleteCloud_PartialMaybe_Title", "Runs_DeleteCloud_PartialMaybe_Body_Quota", ToastSeverity.Warning),
        CloudErrorKind.Network => Pair("Runs_DeleteCloud_PartialMaybe_Title", "Runs_DeleteCloud_PartialMaybe_Body_Network", ToastSeverity.Warning),
        _ => Pair("Runs_DeleteCloud_PartialMaybe_Title", "Runs_DeleteCloud_PartialMaybe_Body_Other", ToastSeverity.Warning),
    };

    /// <summary>
    /// A cloud delete that stopped with files known to remain (the body takes the count as {0}) for a cause that pressing the button again cannot fix by itself:
    /// the count and that cause's one action. Null for a cause whose action is just "choose it again" (the general count copy already says so).
    /// </summary>
    public static RunsMessage? DeleteCloudPartialKnown(CloudErrorKind kind) => kind switch
    {
        CloudErrorKind.Permission => Pair("Runs_DeleteCloud_Partial_Title", "Runs_DeleteCloud_PartialKnown_Body_Permission", ToastSeverity.Warning),
        CloudErrorKind.OrgPolicy => Pair("Runs_DeleteCloud_Partial_Title", "Runs_DeleteCloud_PartialKnown_Body_OrgPolicy", ToastSeverity.Warning),
        CloudErrorKind.Billing => Pair("Runs_DeleteCloud_Partial_Title", "Runs_DeleteCloud_PartialKnown_Body_Billing", ToastSeverity.Warning),
        CloudErrorKind.ApiDisabled => Pair("Runs_DeleteCloud_Partial_Title", "Runs_DeleteCloud_PartialKnown_Body_Api", ToastSeverity.Warning),
        CloudErrorKind.Quota => Pair("Runs_DeleteCloud_Partial_Title", "Runs_DeleteCloud_PartialKnown_Body_Quota", ToastSeverity.Warning),
        _ => null,
    };

    /// <summary>A cloud delete whose sweep finished without error but whose read-back could not be done: the app does not know the folder is empty, and does not claim the delete stopped.</summary>
    public static readonly RunsMessage DeleteCloudPartialUnconfirmed = Pair("Runs_DeleteCloud_PartialUnconfirmed_Title", "Runs_DeleteCloud_PartialUnconfirmed_Body", ToastSeverity.Warning);

    /// <summary>One file left in storage after a partial cloud delete (singular body).</summary>
    public static readonly RunsMessage DeleteCloudPartialOne = Pair("Runs_DeleteCloud_Partial_Title", "Runs_DeleteCloud_Partial_Body_One", ToastSeverity.Warning);

    /// <summary>A cloud delete whose storage listing never finished (it repeated a page token): the app cannot tell what is left. Pressing the button again does not help, so the one action is the diagnostics file.</summary>
    public static readonly RunsMessage DeleteCloudListingStuck = Pair("Runs_DeleteCloud_ListingStuck_Title", "Runs_DeleteCloud_ListingStuck_Body", ToastSeverity.Warning);

    /// <summary>The same stuck listing when the delete failed before any delete was requested: nothing was deleted, the app just cannot see what is stored. One action: the diagnostics file.</summary>
    public static readonly RunsMessage DeleteCloudListingStuckNothingDeleted = Pair("Runs_DeleteCloud_ListingStuck_Title", "Runs_DeleteCloud_ListingStuckNothing_Body", ToastSeverity.Warning);

    public static RunsMessage DeleteCloud(CloudResultsStatus status) => status switch
    {
        CloudResultsStatus.OtherComputer => Pair("Runs_DeleteCloud_OtherComputer_Title", "Runs_DeleteCloud_OtherComputer_Body", ToastSeverity.Warning),
        CloudResultsStatus.NotConfirmed => Pair("Runs_DeleteCloud_NotConfirmed_Title", "Runs_DeleteCloud_NotConfirmed_Body", ToastSeverity.Warning),
        CloudResultsStatus.StorageNotFound => Pair("Runs_DeleteCloud_StorageNotFound_Title", "Runs_DeleteCloud_StorageNotFound_Body", ToastSeverity.Warning),
        CloudResultsStatus.Done => Pair("Runs_DeleteCloud_Done_Title", "Runs_DeleteCloud_Done_Body", ToastSeverity.Success),
        CloudResultsStatus.NoCloudCopy => Pair("Runs_DeleteCloud_NoCloudCopy_Title", "Runs_DeleteCloud_NoCloudCopy_Body", ToastSeverity.Info),
        CloudResultsStatus.Refused => Pair("Runs_DeleteCloud_Refused_Title", "Runs_DeleteCloud_Refused_Body", ToastSeverity.Error),
        CloudResultsStatus.NotConnected => Pair("Runs_DeleteCloud_NotConnected_Title", "Runs_DeleteCloud_NotConnected_Body", ToastSeverity.Warning),
        CloudResultsStatus.PartiallyDeleted => Pair("Runs_DeleteCloud_Partial_Title", "Runs_DeleteCloud_Partial_Body", ToastSeverity.Warning),
        _ => Pair("Runs_DeleteCloud_Failed_Title", "Runs_DeleteCloud_Failed_Body", ToastSeverity.Error),
    };

    /// <summary>A cloud delete that stopped partway and could not read how many objects remain (so no count is promised).</summary>
    public static readonly RunsMessage DeleteCloudPartialUnknown = Pair("Runs_DeleteCloud_PartialMaybe_Title", "Runs_DeleteCloud_PartialUnknown_Body", ToastSeverity.Warning);

    public static RunsMessage DeleteLocal(LocalDeleteStatus status) => status switch
    {
        LocalDeleteStatus.Deleted => Pair("Runs_DeleteLocal_Done_Title", "Runs_DeleteLocal_Done_Body", ToastSeverity.Success),
        LocalDeleteStatus.NothingToDelete => Pair("Runs_DeleteLocal_Nothing_Title", "Runs_DeleteLocal_Nothing_Body", ToastSeverity.Info),
        LocalDeleteStatus.InUse => Pair("Runs_DeleteLocal_InUse_Title", "Runs_DeleteLocal_InUse_Body", ToastSeverity.Error),
        LocalDeleteStatus.AccessDenied => Pair("Runs_DeleteLocal_AccessDenied_Title", "Runs_DeleteLocal_AccessDenied_Body", ToastSeverity.Error),
        LocalDeleteStatus.Partial => Pair("Runs_DeleteLocal_Partial_Title", "Runs_DeleteLocal_Partial_Body", ToastSeverity.Warning),
        LocalDeleteStatus.LinkRefused => Pair("Runs_DeleteLocal_Link_Title", "Runs_DeleteLocal_Link_Body", ToastSeverity.Error),
        LocalDeleteStatus.NeedsConfirmation => Pair("Runs_DeleteLocal_NeedsConfirmation_Title", "Runs_DeleteLocal_NeedsConfirmation_Body", ToastSeverity.Warning),
        LocalDeleteStatus.OtherRunsFolder => Pair("Runs_DeleteLocal_OtherRun_Title", "Runs_DeleteLocal_OtherRun_Body", ToastSeverity.Error),
        LocalDeleteStatus.DriveRootRefused => Pair("Runs_DeleteLocal_DriveRoot_Title", "Runs_DeleteLocal_DriveRoot_Body", ToastSeverity.Error),
        LocalDeleteStatus.ProtectedFolderRefused => Pair("Runs_DeleteLocal_Protected_Title", "Runs_DeleteLocal_Protected_Body", ToastSeverity.Error),
        _ => Pair("Runs_DeleteLocal_Refused_Title", "Runs_DeleteLocal_Refused_Body", ToastSeverity.Error),
    };

    /// <summary>
    /// The same refusals as <see cref="DeleteLocal"/> for a host with no dialog window: a toast has no Open folder button, so these name the row's own Open button instead.
    /// A status with no such variant answers the ordinary copy.
    /// </summary>
    public static RunsMessage DeleteLocalAsToast(LocalDeleteStatus status) => status switch
    {
        LocalDeleteStatus.OtherRunsFolder => Pair("Runs_DeleteLocal_OtherRun_Title", "Runs_DeleteLocal_OtherRun_Toast_Body", ToastSeverity.Error),
        LocalDeleteStatus.DriveRootRefused => Pair("Runs_DeleteLocal_DriveRoot_Title", "Runs_DeleteLocal_DriveRoot_Toast_Body", ToastSeverity.Error),
        LocalDeleteStatus.ProtectedFolderRefused => Pair("Runs_DeleteLocal_Protected_Title", "Runs_DeleteLocal_Protected_Toast_Body", ToastSeverity.Error),
        _ => DeleteLocal(status),
    };

    /// <summary>A delete that stopped on a link after it had already deleted some files (issue #934): says some files are gone and the rest were left, never "nothing was deleted". One action: Open folder.</summary>
    public static readonly RunsMessage DeleteLocalLinkPartial = Pair("Runs_DeleteLocal_LinkPartial_Title", "Runs_DeleteLocal_LinkPartial_Body", ToastSeverity.Error);

    /// <summary>The toast form of <see cref="DeleteLocalLinkPartial"/>: a toast has no Open folder button, so it names the row's Open button instead.</summary>
    public static readonly RunsMessage DeleteLocalLinkPartialAsToast = Pair("Runs_DeleteLocal_LinkPartial_Title", "Runs_DeleteLocal_LinkPartial_Toast_Body", ToastSeverity.Error);

    /// <summary>The toast form of <see cref="Redownload"/> for a run saved straight to a drive (see <see cref="DeleteLocalAsToast"/>).</summary>
    public static readonly RunsMessage RedownloadDriveRootAsToast = Pair("Runs_Redownload_DriveRoot_Title", "Runs_Redownload_DriveRoot_Toast_Body", ToastSeverity.Error);

    /// <summary>Every key above, for the guard that proves each one exists in the .resw.</summary>
    public static IReadOnlyList<string> AllKeys { get; } =
    [
        EmptyNone, EmptyNoMatch, GroupToday, GroupYesterday,
        StatusActive, StatusCompleted, StatusPartial, StatusFailed, StatusCancelled,
        OwnerRequestActAs, OwnerRequestGrantee, PresetLine, DeleteCloudHintRunning, DeleteCloudHintNoCopy, DeleteCloudHintNotConnected, DeleteCloudConfirmTitle, DeleteCloudConfirmBody, DeleteLocalConfirmTitle, DeleteLocalConfirmBody, RemoveConfirmTitle, RemoveConfirmBody,
        DeleteCloudHintImported, DeleteCloudHintPrototype, DeleteCloudHintImportedNoLink, DeleteCloudHintPrototypeNoFolder, ImportedBadge, ImportRunning, ImportNone, ImportDoneOne, ImportDoneMany, ImportNoProject, ImportFailed, ImportFailedPermission, ImportFailedSetup, ImportFailedNetwork, ImportFailedSignIn,
        RedownloadAgainLabel, RedownloadPartialLabel, .. Flatten(RedownloadCancelledPartial),
        .. FilterKeys, DeleteLocalHintImported,
        DeleteLocalUnmarkedConfirmTitle, DeleteLocalUnmarkedConfirmBody, OpenFolderLabel, CloseLabel, DeleteLocalHintDriveRoot, RedownloadHintDriveRoot,
        .. Flatten(OwnerRequestCopied), .. Flatten(OwnerRequestCopyFailed), .. Flatten(OpenMissing), .. Flatten(RerunNoOptions), .. Flatten(RerunNoInput), .. Flatten(RerunFailed), .. Flatten(RemoveFailed), .. Flatten(RefreshFailed), .. Flatten(RedownloadChangedKeptAside(1)), .. Flatten(RedownloadChangedKeptAside(2)),
        .. Enum.GetValues<CloudResultsStatus>().SelectMany(s => Flatten(Redownload(s))).Distinct(),
        .. Enum.GetValues<CloudResultsStatus>().SelectMany(s => Flatten(DeleteCloud(s))).Distinct(),
        .. Flatten(DeleteCloudListingStuck), .. Flatten(DeleteCloudListingStuckNothingDeleted), .. Flatten(DeleteCloudPartialUnknown), .. Flatten(DeleteCloudPartialOne), .. Flatten(DeleteCloudPartialUnconfirmed),
        .. Enum.GetValues<CloudErrorKind>().Select(DeleteCloudPartialKnown).Where(m => m is not null).SelectMany(m => Flatten(m!.Value)).Distinct(),
        .. Enum.GetValues<CloudErrorKind>().SelectMany(k => Flatten(DeleteCloudFailed(k))).Distinct(),
        .. Enum.GetValues<CloudErrorKind>().SelectMany(k => Flatten(DeleteCloudPartialMaybe(k))).Distinct(),
        .. Enum.GetValues<LocalDeleteStatus>().SelectMany(s => Flatten(DeleteLocal(s))).Distinct(),
        .. Enum.GetValues<LocalDeleteStatus>().SelectMany(s => Flatten(DeleteLocalAsToast(s))).Distinct(),
        .. Flatten(RedownloadDriveRootAsToast), .. Flatten(DeleteLocalLinkPartial), .. Flatten(DeleteLocalLinkPartialAsToast),
    ];

    // Whole literal keys on purpose: scripts/check_app_wiring.py finds a resource's reader by its literal text.
    private static RunsMessage Pair(string title, string body, ToastSeverity severity) => new(title, body, severity);

    private static string[] Flatten(RunsMessage pair) => [pair.Title, pair.Body];
}
