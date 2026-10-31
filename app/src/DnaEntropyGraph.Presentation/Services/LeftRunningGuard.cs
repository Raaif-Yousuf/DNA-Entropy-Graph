using System.Globalization;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;

namespace DnaEntropyGraph.Presentation.Services;

/// <summary>
/// Issue #113: nothing is left running and billing without the user knowing. Three triggers share one <see cref="LeftRunningScanner"/>: the launch scan and a scan every
/// <see cref="Period"/> (a computer of this installation that is running with no live run attached gets a warning, once, naming it and where to stop it), and
/// <see cref="ConfirmExitAsync"/> when the window is closing (a modal that names each running computer with the minutes before it ends itself at its time limit and what
/// it costs: Stop, Delete or Leave running). "Leave running" is never indefinite: every computer carries a <c>maxRunDuration</c> (Hard Rules 10 and 11), and the text says so.
/// Only this installation's resources are ever named or touched (Hard Rule 9: the scanner lists by label). A scan that cannot read Google never blocks closing the app.
/// Every string is a Resources.resw key (Hard Rule 13); nothing logs a name, a project or an email.
/// </summary>
public sealed class LeftRunningGuard : IDisposable
{
    /// <summary>How often the background scan runs.</summary>
    public static readonly TimeSpan Period = TimeSpan.FromMinutes(5);

    /// <summary>The longest a scan may take (a captive network can hang a listing forever): past it the scan is abandoned, and closing the app is allowed.</summary>
    public static readonly TimeSpan ScanTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The longest the exit waits for a Stop or Delete to be accepted: past it the window closes and the call finishes on Google's side.</summary>
    public static readonly TimeSpan EndTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The longest the exit waits behind another open dialog before giving up on showing its own.</summary>
    public static readonly TimeSpan DialogGateTimeout = TimeSpan.FromSeconds(10);

    private readonly LeftRunningScanner _scanner;
    private readonly InstallationCleanup _cleanup;
    private readonly ISettingsStore _settings;
    private readonly IChoiceDialogService _dialog;
    private readonly IToastService _toasts;
    private readonly IStringResourceProvider _strings;
    private readonly IDiagnosticsLog _log;
    private readonly TimeProvider _clock;
    private readonly IGcpAccount _account;
    private readonly HashSet<(string Name, string Zone)> _announced = [];
    private readonly object _gate = new();
    private ITimer? _timer;
    private bool _scanning;

    public LeftRunningGuard(LeftRunningScanner scanner, InstallationCleanup cleanup, ISettingsStore settings, IChoiceDialogService dialog, IToastService toasts, IStringResourceProvider strings, IDiagnosticsLog log, TimeProvider clock, IGcpAccount account)
    {
        _account = account;
        _scanner = scanner;
        _cleanup = cleanup;
        _settings = settings;
        _dialog = dialog;
        _toasts = toasts;
        _strings = strings;
        _log = log;
        _clock = clock;
    }

    /// <summary>The most recent timer-driven scan (completed when none has run yet), so a test can wait for it. Never faults.</summary>
    public Task LastTickAsync { get; private set; } = Task.CompletedTask;

    /// <summary>Scans once now (launch) and then every <see cref="Period"/>. Safe to call twice: the second call does nothing. Never faults.</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_timer is not null)
            {
                return;
            }

            _timer = _clock.CreateTimer(_ => LastTickAsync = ScanAndWarnAsync(CancellationToken.None), null, Period, Period);
        }

        await ScanAndWarnAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    private async Task<LeftRunningReport> BoundedScanAsync(CancellationToken cancellationToken)
    {
        // Issue #830: the scan lists in every scope a recent run of this installation was made in plus the selection (the selection path); what it finds carries the scope it was found in,
        // and every stop or delete below uses that one. A signed-out selection still scans the scopes of recent runs.
        var scan = _scanner.ScanAllAsync(_account.SelectedScope(), cancellationToken);
        try
        {
            var report = await scan.WaitAsync(ScanTimeout, _clock, cancellationToken).ConfigureAwait(false);
            if (report.UnreadScopes > 0)
            {
                _log.Warning("left-running-scan", null, "unread-scopes");
            }

            return report;
        }
        catch (TimeoutException)
        {
            ObserveLate(scan, "left-running-scan", null);
            throw;
        }
    }

    /// <summary>
    /// A call the timeout abandoned may still fail later: its fault is observed and logged by class (once, with the job id when there is one) so it never goes unobserved.
    /// Only the abandoned call is observed here: a fault the caller awaits is logged by the caller, so one fault is one log line.
    /// </summary>
    private void ObserveLate(Task task, string source, string? jobId)
        => _ = task.ContinueWith(
            t => _log.Warning(source, jobId, t.Exception?.GetBaseException().GetType().Name ?? nameof(Exception)),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private async Task ScanAndWarnAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            // A slow listing must not stack up behind the next tick.
            if (_scanning)
            {
                return;
            }

            _scanning = true;
        }

        try
        {
            var report = await BoundedScanAsync(cancellationToken).ConfigureAwait(false);
            var orphans = report.RunningVms.Where(v => !v.HasLiveRun).ToList();
            List<LeftRunningVm> fresh;
            lock (_gate)
            {
                // A computer that ended and later came back is announced again.
                _announced.RemoveWhere(k => !orphans.Any(v => v.Name == k.Name && v.Zone == k.Zone));
                fresh = orphans.Where(v => _announced.Add((v.Name, v.Zone))).ToList();
            }

            if (fresh.Count == 1)
            {
                _toasts.ShowToast(Text("LeftRunning_ToastTitle"), Text("LeftRunning_ToastBody", fresh[0].Name, fresh[0].Zone), ToastSeverity.Warning);
            }
            else if (fresh.Count > 1)
            {
                // The message bar holds one message, so every computer found in one scan is ONE toast: the count and the first names.
                var names = fresh.Select(v => v.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
                var shown = string.Join(", ", names.Take(3));
                if (names.Count > 3)
                {
                    shown += " " + Text("LeftRunning_ToastMore", (names.Count - 3).ToString(CultureInfo.InvariantCulture));
                }

                _toasts.ShowToast(Text("LeftRunning_ToastTitle"), Text("LeftRunning_ToastBodyMany", fresh.Count.ToString(CultureInfo.InvariantCulture), shown), ToastSeverity.Warning);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The scan is a safety net; a failed read is tried again at the next tick.
            _log.Warning("left-running-scan", null, ex.GetType().Name);
        }
        finally
        {
            lock (_gate)
            {
                _scanning = false;
            }
        }
    }

    /// <summary>
    /// The window is closing: true when it may close. Nothing running, a scan that could not read Google, a dialog that could not be shown, "Leave running" and a finished
    /// Stop or Delete all allow it; a Stop or Delete that failed for some computer says so, names the action, and keeps the window open (the next close asks again).
    /// </summary>
    public async Task<bool> ConfirmExitAsync(CancellationToken cancellationToken)
    {
        LeftRunningReport report;
        try
        {
            report = await BoundedScanAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warning("left-running-exit", null, ex.GetType().Name);
            return true;
        }

        if (report.RunningVms.Count == 0)
        {
            return true;
        }

        var lines = string.Join(Environment.NewLine, report.RunningVms.Select(LineOf));
        var body = Text("LeftRunning_ExitBody", lines, DiskNote(report.StoppedDisks));
        var choice = await _dialog.ChooseAsync(
            Text("LeftRunning_ExitTitle"),
            body,
            [Text("LeftRunning_Stop"), Text("LeftRunning_Delete"), Text("LeftRunning_Leave")],
            DialogGateTimeout,
            cancellationToken).ConfigureAwait(false);

        return choice switch
        {
            0 => await EndAllAsync(report.RunningVms, false, "LeftRunning_StopFailedTitle", "LeftRunning_StopFailedBody", cancellationToken).ConfigureAwait(false),
            1 => await EndAllAsync(report.RunningVms, true, "LeftRunning_DeleteFailedTitle", "LeftRunning_DeleteFailedBody", cancellationToken).ConfigureAwait(false),
            _ => true,
        };
    }

    private enum EndResult
    {
        Done,
        Failed,
        StillEnding,
    }

    /// <summary>
    /// Stops or deletes every named computer at once through <see cref="InstallationCleanup.EndVmAsync"/>, each bounded by <see cref="EndTimeout"/>. That call SENDS the Compute request
    /// first and ends a live run's row (Hard Rule 11). "Finishing on Google's side" is claimed only when the request was accepted before the bound (or the same job is already being
    /// ended); a request that was never accepted is a failure that keeps the window open.
    /// </summary>
    private async Task<bool> EndAllAsync(IReadOnlyList<LeftRunningVm> vms, bool delete, string failureTitleKey, string failureBodyKey, CancellationToken cancellationToken)
    {
        var installation = InstallationId.GetOrCreate(_settings);
        var results = await Task.WhenAll(vms.Select(async vm =>
        {
            var accepted = false;
            Task<VmEndResult>? work = null;
            try
            {
                // Issue #830: each computer is ended in the scope it was found in, never in whatever is selected by the time the user clicks.
                var scope = vm.Scope ?? _account.SelectedScope();
                if (scope is null)
                {
                    return (vm, EndResult.Failed);
                }

                work = _cleanup.EndVmAsync(scope, installation, vm.Descriptor, delete, () => Volatile.Write(ref accepted, true), cancellationToken);
                var result = await work.WaitAsync(EndTimeout, _clock, cancellationToken).ConfigureAwait(false);
                return (vm, result switch
                {
                    VmEndResult.Done => EndResult.Done,
                    VmEndResult.AlreadyEnding => EndResult.StillEnding,
                    _ => EndResult.Failed,
                });
            }
            catch (TimeoutException)
            {
                if (work is not null)
                {
                    ObserveLate(work, "left-running-exit", vm.JobId);
                }

                return (vm, Volatile.Read(ref accepted) ? EndResult.StillEnding : EndResult.Failed);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Warning("left-running-exit", vm.JobId, ex.GetType().Name);
                return (vm, EndResult.Failed);
            }
        })).ConfigureAwait(false);

        var failed = results.Where(r => r.Item2 == EndResult.Failed).Select(r => r.vm.Name).ToList();
        if (failed.Count > 0)
        {
            _toasts.ShowToast(Text(failureTitleKey), Text(failureBodyKey, string.Join(", ", failed)), ToastSeverity.Error);
            return false;
        }

        var pending = results.Where(r => r.Item2 == EndResult.StillEnding).Select(r => r.vm.Name).ToList();
        if (pending.Count > 0)
        {
            _toasts.ShowToast(Text("LeftRunning_StillEndingTitle"), Text("LeftRunning_StillEndingBody", string.Join(", ", pending)), ToastSeverity.Info);
        }

        return true;
    }

    private string LineOf(LeftRunningVm vm)
    {
        var cost = vm.UsdPerHour is { } usd ? Text("LeftRunning_CostPerHour", Money(usd)) : Text("LeftRunning_CostUnknown");
        if (vm.Remaining is not { } remaining)
        {
            return Text("LeftRunning_VmLineNoLimit", vm.Name, vm.Zone, cost);
        }

        var minutes = ((int)Math.Ceiling(remaining.TotalMinutes)).ToString(CultureInfo.InvariantCulture);
        return Text(vm.HasLiveRun ? "LeftRunning_VmLineLive" : "LeftRunning_VmLine", vm.Name, vm.Zone, minutes, cost);
    }

    private string DiskNote(IReadOnlyList<LeftStoppedDisk> disks)
    {
        if (disks.Count == 0)
        {
            return string.Empty;
        }

        var count = disks.Count.ToString(CultureInfo.InvariantCulture);
        return disks.All(d => d.UsdPerMonth is not null)
            ? Text("LeftRunning_DiskNote", count, Money(disks.Sum(d => d.UsdPerMonth!.Value)))
            : Text("LeftRunning_DiskNoteNoPrice", count);
    }

    private string Text(string key, params string[] args)
        => args.Length == 0 ? _strings.GetString(key) : string.Format(CultureInfo.CurrentCulture, _strings.GetString(key), args);

    private static string Money(double usd) => usd.ToString("F2", CultureInfo.InvariantCulture);
}
