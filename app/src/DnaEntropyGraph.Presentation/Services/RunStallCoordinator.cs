using System.Globalization;
using CommunityToolkit.Mvvm.Messaging;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Presentation.Messaging;

namespace DnaEntropyGraph.Presentation.Services;

/// <summary>
/// The app's answer to the runner's two hooks (issue #675): <see cref="OnNotice"/> turns a slow-stage or stall event into a line on the run page, and
/// <see cref="AskAsync"/> is the Stop / Delete / Keep waiting question when the heartbeat watch fires. Both replace the safe production defaults
/// (<see cref="StallDecisions.EndAsRunChose"/> and a log line). A question that cannot be shown or was closed by the runner answers
/// <see cref="StallChoice.KeepWaiting"/>: nothing is ended without the user having chosen it (the runner's own answer timeout and run time limit still end a run nobody answers).
/// Every string is a Resources.resw key (Hard Rule 13); nothing here logs a sequence, a file name or an email.
/// </summary>
public sealed class RunStallCoordinator
{
    private readonly IChoiceDialogService _dialog;
    private readonly IMessenger _messenger;
    private readonly IStringResourceProvider _strings;
    private readonly IDiagnosticsLog _log;

    public RunStallCoordinator(IChoiceDialogService dialog, IMessenger messenger, IStringResourceProvider strings, IDiagnosticsLog log)
    {
        _dialog = dialog;
        _messenger = messenger;
        _strings = strings;
        _log = log;
    }

    /// <summary>The <see cref="StallDecision"/> the runner calls. Raised on a worker thread; the dialog service marshals to the UI thread.</summary>
    public async Task<StallChoice> AskAsync(StallPrompt prompt, CancellationToken cancellationToken)
    {
        var stage = prompt.Stage ?? _strings.GetString("RunNotice_UnknownStage");
        var minutes = Math.Max(1, (int)Math.Round(prompt.Waited.TotalMinutes));
        var body = string.Format(CultureInfo.CurrentCulture, _strings.GetString(BodyKey(prompt.Code)), stage, minutes);
        IReadOnlyList<string> labels =
        [
            _strings.GetString("StallDialog_Stop"),
            _strings.GetString("StallDialog_Delete"),
            _strings.GetString("StallDialog_KeepWaiting"),
        ];
        try
        {
            var pressed = await _dialog.ChooseAsync(_strings.GetString("StallDialog_Title"), body, labels, cancellationToken).ConfigureAwait(false);
            return pressed switch
            {
                0 => StallChoice.Stop,
                1 => StallChoice.Delete,
                _ => StallChoice.KeepWaiting,
            };
        }
        catch (OperationCanceledException)
        {
            // The worker recovered or the run ended while the question was open: the runner withdrew it.
            return StallChoice.KeepWaiting;
        }
    }

    /// <summary>The <see cref="CloudJobRunner.OnNotice"/> hook: publishes the line for the run page and keeps the by-kind log line the default had.</summary>
    public void OnNotice(RunNotice notice)
    {
        _log.Warning("run-notice", notice.JobId, notice.Kind.ToString());
        var stage = notice.Stage ?? _strings.GetString("RunNotice_UnknownStage");
        var text = notice.Kind switch
        {
            RunNoticeKind.SlowStage => string.Format(CultureInfo.CurrentCulture, _strings.GetString("RunNotice_Slow"), stage, Math.Max(1, (int)Math.Round(notice.InStage.TotalMinutes))),
            RunNoticeKind.StallDetected => string.Format(CultureInfo.CurrentCulture, _strings.GetString("RunNotice_Stalled"), stage, 0),
            _ => string.Empty,
        };
        _messenger.Send(new RunNoticeMessage(notice.JobId, text));
    }

    // A literal per code so the orphan-resource scan sees every key (scripts/check_app_wiring.py).
    private static string BodyKey(string code) => code switch
    {
        RunErrorCodes.WorkerHeartbeatStale => "StallDialog_Body_Stale",
        RunErrorCodes.WorkerNoHeartbeat => "StallDialog_Body_NoHeartbeat",
        _ => "StallDialog_Body_Stuck",
    };
}
