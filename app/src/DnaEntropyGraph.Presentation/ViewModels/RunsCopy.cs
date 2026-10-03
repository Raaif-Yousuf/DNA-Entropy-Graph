using DnaEntropyGraph.Core.Abstractions;
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
    public const string DeleteCloudHintRunning = "Runs_DeleteCloud_Hint_Running";
    public const string DeleteCloudHintNoCopy = "Runs_DeleteCloud_Hint_NoCopy";
    public const string DeleteCloudHintNotConnected = "Runs_DeleteCloud_Hint_NotConnected";
    public const string RemoveConfirmTitle = "Runs_Remove_Confirm_Title";
    public const string RemoveConfirmBody = "Runs_Remove_Confirm_Body";

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

    public static RunsMessage Redownload(CloudResultsStatus status) => status switch
    {
        CloudResultsStatus.Done => Pair("Runs_Redownload_Done_Title", "Runs_Redownload_Done_Body", ToastSeverity.Success),
        CloudResultsStatus.Partial => Pair("Runs_Redownload_Partial_Title", "Runs_Redownload_Partial_Body", ToastSeverity.Warning),
        CloudResultsStatus.Expired => Pair("Runs_Redownload_Expired_Title", "Runs_Redownload_Expired_Body", ToastSeverity.Warning),
        CloudResultsStatus.NoCloudCopy => Pair("Runs_Redownload_NoCloudCopy_Title", "Runs_Redownload_NoCloudCopy_Body", ToastSeverity.Info),
        CloudResultsStatus.ResultNotFound => Pair("Runs_Redownload_ResultNotFound_Title", "Runs_Redownload_ResultNotFound_Body", ToastSeverity.Error),
        CloudResultsStatus.Refused => Pair("Runs_Redownload_Refused_Title", "Runs_Redownload_Refused_Body", ToastSeverity.Error),
        _ => Pair("Runs_Redownload_Failed_Title", "Runs_Redownload_Failed_Body", ToastSeverity.Error),
    };

    public static RunsMessage DeleteCloud(CloudResultsStatus status) => status switch
    {
        CloudResultsStatus.Done => Pair("Runs_DeleteCloud_Done_Title", "Runs_DeleteCloud_Done_Body", ToastSeverity.Success),
        CloudResultsStatus.NoCloudCopy => Pair("Runs_DeleteCloud_NoCloudCopy_Title", "Runs_DeleteCloud_NoCloudCopy_Body", ToastSeverity.Info),
        CloudResultsStatus.Refused => Pair("Runs_DeleteCloud_Refused_Title", "Runs_DeleteCloud_Refused_Body", ToastSeverity.Error),
        CloudResultsStatus.NotConnected => Pair("Runs_DeleteCloud_NotConnected_Title", "Runs_DeleteCloud_NotConnected_Body", ToastSeverity.Warning),
        _ => Pair("Runs_DeleteCloud_Failed_Title", "Runs_DeleteCloud_Failed_Body", ToastSeverity.Error),
    };

    public static RunsMessage DeleteLocal(LocalDeleteStatus status) => status switch
    {
        LocalDeleteStatus.Deleted => Pair("Runs_DeleteLocal_Done_Title", "Runs_DeleteLocal_Done_Body", ToastSeverity.Success),
        LocalDeleteStatus.NothingToDelete => Pair("Runs_DeleteLocal_Nothing_Title", "Runs_DeleteLocal_Nothing_Body", ToastSeverity.Info),
        LocalDeleteStatus.InUse => Pair("Runs_DeleteLocal_InUse_Title", "Runs_DeleteLocal_InUse_Body", ToastSeverity.Error),
        LocalDeleteStatus.AccessDenied => Pair("Runs_DeleteLocal_AccessDenied_Title", "Runs_DeleteLocal_AccessDenied_Body", ToastSeverity.Error),
        LocalDeleteStatus.Partial => Pair("Runs_DeleteLocal_Partial_Title", "Runs_DeleteLocal_Partial_Body", ToastSeverity.Warning),
        LocalDeleteStatus.LinkRefused => Pair("Runs_DeleteLocal_Link_Title", "Runs_DeleteLocal_Link_Body", ToastSeverity.Error),
        _ => Pair("Runs_DeleteLocal_Refused_Title", "Runs_DeleteLocal_Refused_Body", ToastSeverity.Error),
    };

    /// <summary>Every key above, for the guard that proves each one exists in the .resw.</summary>
    public static IReadOnlyList<string> AllKeys { get; } =
    [
        EmptyNone, EmptyNoMatch, GroupToday, GroupYesterday,
        StatusActive, StatusCompleted, StatusPartial, StatusFailed, StatusCancelled,
        DeleteCloudHintRunning, DeleteCloudHintNoCopy, DeleteCloudHintNotConnected, DeleteCloudConfirmTitle, DeleteCloudConfirmBody, DeleteLocalConfirmTitle, DeleteLocalConfirmBody, RemoveConfirmTitle, RemoveConfirmBody,
        .. FilterKeys,
        .. Flatten(OpenMissing), .. Flatten(RerunNoOptions), .. Flatten(RerunNoInput), .. Flatten(RerunFailed), .. Flatten(RemoveFailed), .. Flatten(RefreshFailed), .. Flatten(RedownloadChangedKeptAside(1)), .. Flatten(RedownloadChangedKeptAside(2)),
        .. Enum.GetValues<CloudResultsStatus>().SelectMany(s => Flatten(Redownload(s))).Distinct(),
        .. Enum.GetValues<CloudResultsStatus>().SelectMany(s => Flatten(DeleteCloud(s))).Distinct(),
        .. Enum.GetValues<LocalDeleteStatus>().SelectMany(s => Flatten(DeleteLocal(s))).Distinct(),
    ];

    // Whole literal keys on purpose: scripts/check_app_wiring.py finds a resource's reader by its literal text.
    private static RunsMessage Pair(string title, string body, ToastSeverity severity) => new(title, body, severity);

    private static string[] Flatten(RunsMessage pair) => [pair.Title, pair.Body];
}
