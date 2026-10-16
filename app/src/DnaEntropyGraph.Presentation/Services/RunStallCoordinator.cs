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

    // Per run: whether a question is open and which kind of notice the page shows now. Guarded by itself: AskAsync and OnNotice run on the runner's threads.
    private readonly Dictionary<string, RunState> _runs = [];

    private sealed class RunState
    {
        public bool QuestionOpen { get; set; }

        public RunNoticeKind? Shown { get; set; }
    }

    private RunState StateOf(string jobId)
    {
        if (!_runs.TryGetValue(jobId, out var state))
        {
            _runs[jobId] = state = new RunState();
        }

        return state;
    }

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
        lock (_runs)
        {
            StateOf(prompt.JobId).QuestionOpen = true;
        }

        try
        {
            return await AskCoreAsync(prompt, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // The runner sends no StallCleared after Keep waiting (it only snoozes its watch), so the "a window is asking" line must go when the question ends (#740). Only
            // that line: a newer notice for the run (a slow step raised while the question was open) stays, and a line the runner already cleared needs no second clear.
            lock (_runs)
            {
                var state = StateOf(prompt.JobId);
                state.QuestionOpen = false;
                if (state.Shown is null or RunNoticeKind.StallDetected)
                {
                    state.Shown = RunNoticeKind.StallCleared;
                    _messenger.Send(new RunNoticeMessage(prompt.JobId, string.Empty));
                }
            }
        }
    }

    private async Task<StallChoice> AskCoreAsync(StallPrompt prompt, CancellationToken cancellationToken)
    {
        var stage = prompt.Stage ?? _strings.GetString("RunNotice_UnknownStage");
        var minutes = Math.Max(1, (int)Math.Round(prompt.Waited.TotalMinutes));
        var computer = prompt.VmName ?? _strings.GetString("StallDialog_ThisComputer");
        var run = prompt.RunName is { Length: > 0 } name
            ? string.Format(CultureInfo.CurrentCulture, _strings.GetString("StallDialog_RunLabel"), name, prompt.JobId)
            : string.Format(CultureInfo.CurrentCulture, _strings.GetString("StallDialog_RunLabelNoName"), prompt.JobId);

        // {0} step, {1} minutes, {2} the rented computer, {3} the run: every template of this dialog may use any of them.
        string Fill(string key) => string.Format(CultureInfo.CurrentCulture, _strings.GetString(key), stage, minutes, computer, run);
        var title = Fill("StallDialog_Title");
        var body = Fill(BodyKey(prompt.Code));
        IReadOnlyList<string> labels =
        [
            _strings.GetString("StallDialog_Stop"),
            _strings.GetString("StallDialog_Delete"),
            _strings.GetString("StallDialog_KeepWaiting"),
        ];
        try
        {
            var pressed = await _dialog.ChooseAsync(title, body, labels, cancellationToken).ConfigureAwait(false);
            if (pressed == 1)
            {
                // Delete destroys the computer and its disk: it needs its own question that names the computer. Anything but its Delete button keeps waiting.
                IReadOnlyList<string> confirmLabels = [_strings.GetString("StallConfirm_Delete"), _strings.GetString("StallDialog_KeepWaiting")];
                var confirmed = await _dialog.ChooseAsync(Fill("StallConfirm_Title"), Fill("StallConfirm_Body"), confirmLabels, cancellationToken).ConfigureAwait(false);
                return confirmed == 0 ? StallChoice.Delete : StallChoice.KeepWaiting;
            }

            return pressed switch
            {
                0 => StallChoice.Stop,
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
        lock (_runs)
        {
            var state = StateOf(notice.JobId);
            if (notice.Kind == RunNoticeKind.StallDetected && !state.QuestionOpen && state.Shown is RunNoticeKind.StallCleared)
            {
                // The question already ended (answered before this announcement was published): its line would never be cleared.
                return;
            }

            state.Shown = notice.Kind;
            Publish(notice);
        }
    }

    private void Publish(RunNotice notice)
    {
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
