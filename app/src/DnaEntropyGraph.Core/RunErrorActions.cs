using DnaEntropyGraph.Core.Cloud;

namespace DnaEntropyGraph.Core;

/// <summary>The one thing a failed run's message asks the user to do (Hard Rule 13, issue #176).</summary>
public enum RunErrorAction
{
    StartAgain,
    WaitAndRetry,
    OpenSettings,
    OpenCloudPage,
    SaveDiagnostics,
    DownloadAgain,
    ChooseFile,
    CheckFile,
    SplitInput,
    ChangeRunOption,
    ChooseCloud,
    ChooseModel,
    ChooseOutputFolder,
    FreeSpace,
    UpdateApp,
    AskAdministrator,
    GoogleConsole,
    CheckInternet,
    CopyOwnerRequest,
    RestoreFile,
    CheckAccession,
    NarrowRange,
}

/// <summary>
/// Which <see cref="RunErrorAction"/> each <see cref="RunErrorCodes"/> code asks for. A guard test
/// (<c>ErrorMessageActionGuardTests</c>) requires a row for every code and requires the code's message to say the action.
/// </summary>
public static class RunErrorActions
{
    private static readonly Dictionary<string, RunErrorAction> Table = new(StringComparer.Ordinal)
    {
        [RunErrorCodes.NoProject] = RunErrorAction.OpenSettings,
        [RunErrorCodes.TargetNotSupported] = RunErrorAction.ChooseCloud,
        [RunErrorCodes.VmUnhealthy] = RunErrorAction.StartAgain,
        [RunErrorCodes.RunTimeLimit] = RunErrorAction.StartAgain,
        [RunErrorCodes.DeletedByCleanup] = RunErrorAction.StartAgain,
        [RunErrorCodes.WorkerStageStuck] = RunErrorAction.StartAgain,
        [RunErrorCodes.WorkerProgressStalled] = RunErrorAction.StartAgain,
        [RunErrorCodes.LifecycleUnverified] = RunErrorAction.OpenCloudPage,
        [RunErrorCodes.CancelFailed] = RunErrorAction.OpenCloudPage,
        [RunErrorCodes.InputMissing] = RunErrorAction.ChooseFile,
        [RunErrorCodes.WorkerImageUnavailable] = RunErrorAction.UpdateApp,
        [RunErrorCodes.WorkerImageRefused] = RunErrorAction.UpdateApp,
        [RunErrorCodes.ResultTimeout] = RunErrorAction.StartAgain,
        [RunErrorCodes.RunTimeLimit] = RunErrorAction.StartAgain,
        [RunErrorCodes.WorkerNoHeartbeat] = RunErrorAction.StartAgain,
        [RunErrorCodes.WorkerHeartbeatStale] = RunErrorAction.StartAgain,
        [RunErrorCodes.WorkerFailed] = RunErrorAction.SaveDiagnostics,
        [RunErrorCodes.DownloadFailed] = RunErrorAction.FreeSpace,
        [RunErrorCodes.DownloadCorrupt] = RunErrorAction.DownloadAgain,
        [RunErrorCodes.VmEndUnconfirmed] = RunErrorAction.OpenCloudPage,
        [RunErrorCodes.GpuNotVisible] = RunErrorAction.StartAgain,
        [RunErrorCodes.ImagePullFailed] = RunErrorAction.WaitAndRetry,
        [RunErrorCodes.ManifestInvalid] = RunErrorAction.SaveDiagnostics,
        [RunErrorCodes.WorkerCrashed] = RunErrorAction.SaveDiagnostics,
        [RunErrorCodes.CloudNotConnected] = RunErrorAction.UpdateApp,
        [RunErrorCodes.ModelNeedsHopper] = RunErrorAction.ChooseModel,
        [RunErrorCodes.ModelOom] = RunErrorAction.ChangeRunOption,
        [RunErrorCodes.BatchLimitExceeded] = RunErrorAction.SplitInput,
        [RunErrorCodes.InputInvalid] = RunErrorAction.CheckFile,
        [RunErrorCodes.WorkerVersionMismatch] = RunErrorAction.UpdateApp,
        [RunErrorCodes.OutputFolderUnusable] = RunErrorAction.ChooseOutputFolder,
        [RunErrorCodes.InputInvalidCharacter] = RunErrorAction.ChooseFile,
        [RunErrorCodes.InputAmbiguityRefused] = RunErrorAction.ChangeRunOption,
        [RunErrorCodes.InputRna] = RunErrorAction.ChangeRunOption,
        [RunErrorCodes.InputNotUtf8] = RunErrorAction.ChooseFile,
        [RunErrorCodes.InputEmpty] = RunErrorAction.ChooseFile,
        [RunErrorCodes.InputTooLong] = RunErrorAction.SplitInput,
        [RunErrorCodes.InputAccessionInvalid] = RunErrorAction.CheckAccession,
        [RunErrorCodes.InputAccessionNotFound] = RunErrorAction.CheckAccession,
        [RunErrorCodes.InputAccessionTooLarge] = RunErrorAction.NarrowRange,
        [RunErrorCodes.InputAccessionUnreachable] = RunErrorAction.CheckInternet,
        [RunErrorCodes.InstallationIdUnusable] = RunErrorAction.RestoreFile,
        [RunErrorCodes.WorkerDefaultAccount] = RunErrorAction.AskAdministrator,
        [RunErrorCodes.PermissionActAs] = RunErrorAction.CopyOwnerRequest,
        [RunErrorCodes.WorkerDefaultAccountMissing] = RunErrorAction.AskAdministrator,
        [RunErrorCodes.WorkerIdentityNotApplied] = RunErrorAction.StartAgain,
        [RunErrorCodes.WorkerStageStuck] = RunErrorAction.StartAgain,
        [RunErrorCodes.DeletedByCleanup] = RunErrorAction.StartAgain,
        [RunErrorCodes.WorkerProgressStalled] = RunErrorAction.StartAgain,
        ["billing"] = RunErrorAction.GoogleConsole,
        ["api_disabled"] = RunErrorAction.GoogleConsole,
        ["quota"] = RunErrorAction.GoogleConsole,
        ["stockout"] = RunErrorAction.WaitAndRetry,
        ["already_exists"] = RunErrorAction.OpenCloudPage,
        ["permission"] = RunErrorAction.OpenSettings,
        ["org_policy"] = RunErrorAction.AskAdministrator,
        ["network"] = RunErrorAction.CheckInternet,
        [RunErrorCodes.Other] = RunErrorAction.SaveDiagnostics,
    };

    /// <summary>Every code with a declared action.</summary>
    public static IReadOnlyCollection<string> Codes => Table.Keys;

    public static bool TryGet(string? code, out RunErrorAction action)
    {
        action = RunErrorAction.SaveDiagnostics;
        return code is not null && Table.TryGetValue(code, out action);
    }

    /// <summary>
    /// The words a message must contain (any one, ignoring case) to be saying <paramref name="action"/>. For a button the
    /// label comes from <paramref name="resources"/> so the message and the button cannot drift apart.
    /// </summary>
    public static IReadOnlyList<string> PhrasesFor(RunErrorAction action, IReadOnlyDictionary<string, string> resources) => action switch
    {
        RunErrorAction.StartAgain => ["start the run again", "run again"],
        RunErrorAction.WaitAndRetry => ["in a few minutes"],
        RunErrorAction.OpenSettings => ["Open Settings", "from Settings"],
        RunErrorAction.OpenCloudPage => ["Cloud page"],
        RunErrorAction.SaveDiagnostics => ["Save diagnostics"],
        RunErrorAction.DownloadAgain => ["download the results again"],
        RunErrorAction.ChooseFile => ["choose the file again"],
        RunErrorAction.CheckFile => ["Check the file"],
        RunErrorAction.SplitInput => ["Split it"],
        RunErrorAction.ChangeRunOption => ["run options"],
        RunErrorAction.ChooseCloud => ["Choose Cloud"],
        RunErrorAction.ChooseModel => ["default model"],
        RunErrorAction.ChooseOutputFolder => ["different output folder"],
        RunErrorAction.FreeSpace => ["Free up space"],
        RunErrorAction.UpdateApp => ["download page"],
        RunErrorAction.AskAdministrator => ["administrator"],
        RunErrorAction.GoogleConsole => ["Google Cloud console"],
        RunErrorAction.CheckInternet => ["internet connection"],
        RunErrorAction.CopyOwnerRequest => [resources["RunsCopyOwnerRequestButton.Content"]],
        RunErrorAction.RestoreFile => ["Restore the id"],
        RunErrorAction.CheckAccession => ["Check the accession"],
        RunErrorAction.NarrowRange => ["smaller range"],
        _ => [],
    };
}
