using DnaEntropyGraph.Core.Runs;

namespace DnaEntropyGraph.Presentation.ViewModels;

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
    public const string RemoveConfirmTitle = "Runs_Remove_Confirm_Title";
    public const string RemoveConfirmBody = "Runs_Remove_Confirm_Body";

    public static readonly (string Title, string Body) OpenMissing = Pair("Runs_Open_Missing_Title", "Runs_Open_Missing_Body");
    public static readonly (string Title, string Body) RerunNoOptions = Pair("Runs_Rerun_NoOptions_Title", "Runs_Rerun_NoOptions_Body");
    public static readonly (string Title, string Body) RerunNoInput = Pair("Runs_Rerun_NoInput_Title", "Runs_Rerun_NoInput_Body");

    private static readonly string[] FilterKeys = ["Runs_Filter_All", "Runs_Filter_Active", "Runs_Filter_Completed", "Runs_Filter_Failed", "Runs_Filter_Cancelled"];

    public static (string Title, string Body) Redownload(CloudResultsStatus status) => status switch
    {
        CloudResultsStatus.Done => Pair("Runs_Redownload_Done_Title", "Runs_Redownload_Done_Body"),
        CloudResultsStatus.Expired => Pair("Runs_Redownload_Expired_Title", "Runs_Redownload_Expired_Body"),
        CloudResultsStatus.NoCloudCopy => Pair("Runs_Redownload_NoCloudCopy_Title", "Runs_Redownload_NoCloudCopy_Body"),
        CloudResultsStatus.ResultNotFound => Pair("Runs_Redownload_ResultNotFound_Title", "Runs_Redownload_ResultNotFound_Body"),
        CloudResultsStatus.Refused => Pair("Runs_Redownload_Refused_Title", "Runs_Redownload_Refused_Body"),
        _ => Pair("Runs_Redownload_Failed_Title", "Runs_Redownload_Failed_Body"),
    };

    public static (string Title, string Body) DeleteCloud(CloudResultsStatus status) => status switch
    {
        CloudResultsStatus.Done => Pair("Runs_DeleteCloud_Done_Title", "Runs_DeleteCloud_Done_Body"),
        CloudResultsStatus.NoCloudCopy => Pair("Runs_DeleteCloud_NoCloudCopy_Title", "Runs_DeleteCloud_NoCloudCopy_Body"),
        CloudResultsStatus.Refused => Pair("Runs_DeleteCloud_Refused_Title", "Runs_DeleteCloud_Refused_Body"),
        CloudResultsStatus.NotConnected => Pair("Runs_DeleteCloud_NotConnected_Title", "Runs_DeleteCloud_NotConnected_Body"),
        _ => Pair("Runs_DeleteCloud_Failed_Title", "Runs_DeleteCloud_Failed_Body"),
    };

    public static (string Title, string Body) DeleteLocal(Core.Runs.LocalDeleteStatus status) => status switch
    {
        LocalDeleteStatus.Deleted => Pair("Runs_DeleteLocal_Done_Title", "Runs_DeleteLocal_Done_Body"),
        LocalDeleteStatus.NothingToDelete => Pair("Runs_DeleteLocal_Nothing_Title", "Runs_DeleteLocal_Nothing_Body"),
        _ => Pair("Runs_DeleteLocal_Refused_Title", "Runs_DeleteLocal_Refused_Body"),
    };

    /// <summary>Every key above, for the guard that proves each one exists in the .resw.</summary>
    public static IReadOnlyList<string> AllKeys { get; } =
    [
        EmptyNone, EmptyNoMatch, GroupToday, GroupYesterday,
        StatusActive, StatusCompleted, StatusPartial, StatusFailed, StatusCancelled,
        DeleteCloudConfirmTitle, DeleteCloudConfirmBody, DeleteLocalConfirmTitle, DeleteLocalConfirmBody, RemoveConfirmTitle, RemoveConfirmBody,
        .. FilterKeys,
        .. Flatten(OpenMissing), .. Flatten(RerunNoOptions), .. Flatten(RerunNoInput),
        .. Enum.GetValues<CloudResultsStatus>().SelectMany(s => Flatten(Redownload(s))).Distinct(),
        .. Enum.GetValues<CloudResultsStatus>().SelectMany(s => Flatten(DeleteCloud(s))).Distinct(),
        .. Enum.GetValues<LocalDeleteStatus>().SelectMany(s => Flatten(DeleteLocal(s))).Distinct(),
    ];

    // Whole literal keys on purpose: scripts/check_app_wiring.py finds a resource's reader by its literal text.
    private static (string Title, string Body) Pair(string title, string body) => (title, body);

    private static string[] Flatten((string Title, string Body) pair) => [pair.Title, pair.Body];
}
