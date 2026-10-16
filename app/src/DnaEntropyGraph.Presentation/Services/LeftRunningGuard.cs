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

    private readonly LeftRunningScanner _scanner;
    private readonly IComputeGateway _compute;
    private readonly IChoiceDialogService _dialog;
    private readonly IToastService _toasts;
    private readonly IStringResourceProvider _strings;
    private readonly IDiagnosticsLog _log;
    private readonly TimeProvider _clock;
    private readonly HashSet<(string Name, string Zone)> _announced = [];
    private readonly object _gate = new();
    private ITimer? _timer;
    private bool _scanning;

    public LeftRunningGuard(LeftRunningScanner scanner, IComputeGateway compute, IChoiceDialogService dialog, IToastService toasts, IStringResourceProvider strings, IDiagnosticsLog log, TimeProvider clock)
    {
        _scanner = scanner;
        _compute = compute;
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
            var report = await _scanner.ScanAsync(cancellationToken).ConfigureAwait(false);
            var orphans = report.RunningVms.Where(v => !v.HasLiveRun).ToList();
            List<LeftRunningVm> fresh;
            lock (_gate)
            {
                // A computer that ended and later came back is announced again.
                _announced.RemoveWhere(k => !orphans.Any(v => v.Name == k.Name && v.Zone == k.Zone));
                fresh = orphans.Where(v => _announced.Add((v.Name, v.Zone))).ToList();
            }

            foreach (var vm in fresh)
            {
                _toasts.ShowToast(Text("LeftRunning_ToastTitle"), Text("LeftRunning_ToastBody", vm.Name, vm.Zone), ToastSeverity.Warning);
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
            report = await _scanner.ScanAsync(cancellationToken).ConfigureAwait(false);
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
            cancellationToken).ConfigureAwait(false);

        return choice switch
        {
            0 => await EndAllAsync(report.RunningVms, (vm, ct) => _compute.StopVmAsync(vm.Name, vm.Zone, ct), "LeftRunning_StopFailed", cancellationToken).ConfigureAwait(false),
            1 => await EndAllAsync(report.RunningVms, (vm, ct) => _compute.DeleteVmAsync(vm.Name, vm.Zone, ct), "LeftRunning_DeleteFailed", cancellationToken).ConfigureAwait(false),
            _ => true,
        };
    }

    private async Task<bool> EndAllAsync(IReadOnlyList<LeftRunningVm> vms, Func<LeftRunningVm, CancellationToken, Task> end, string failureKey, CancellationToken cancellationToken)
    {
        var failed = new List<string>();
        foreach (var vm in vms)
        {
            try
            {
                await end(vm, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Warning("left-running-exit", vm.JobId, ex.GetType().Name);
                failed.Add(vm.Name);
            }
        }

        if (failed.Count == 0)
        {
            return true;
        }

        _toasts.ShowToast(Text(failureKey + "Title"), Text(failureKey + "Body", string.Join(", ", failed)), ToastSeverity.Error);
        return false;
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
