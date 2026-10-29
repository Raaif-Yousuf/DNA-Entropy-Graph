using System.Text.Json;
using DnaEntropyGraph.Core.Abstractions;

namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// Issue #109: the ONE rule for the span of time a run's VM billed, used by every path that looks at a VM (the runner's create and adopt, its resume, the
/// canceller's delete, the reconciler). Start = the VM's own platform start (<see cref="VmDescriptor.RunClockStart"/>) from ANY observation of the VM, settled
/// or not; the app's own clock is never a start. A VM the app never observed has no span. End = the VM's stop time when it was observed stopped; when the VM is
/// gone by the time the app looks (it deleted itself), the last time the app knew it was alive (<see cref="LastKnownAlive"/>); when it is still running, null,
/// which the terminal write fills with "now". The result is stored on the run row (<c>VmReadyAt</c>, <c>FinishedAt</c>) by
/// <see cref="RunRowStore.StampVmSpanAsync"/>; the spend ledger quotes from those two columns and nothing else.
/// </summary>
internal static class VmSpan
{
    /// <summary>The span of an observed VM, or of a VM that is gone (<paramref name="vm"/> null) whose last known sign of life was <paramref name="lastKnownAlive"/>.</summary>
    public static (DateTimeOffset? Start, DateTimeOffset? End) Of(VmDescriptor? vm, DateTimeOffset? lastKnownAlive)
        => vm is null ? (null, lastKnownAlive) : (vm.RunClockStart, EndOfObserved(vm));

    /// <summary>
    /// When billing for a VM that is no longer running ended, or null for a VM that is running (it bills until the run ends). A stopped VM with no stop time
    /// reported ends at its own start: the span is not extended beyond what is known.
    /// </summary>
    public static DateTimeOffset? EndOfObserved(VmDescriptor vm)
        => VmFacts.IsStopped(vm) ? vm.StoppedAt ?? vm.RunClockStart : null;

    /// <summary>The latest of the row's last heartbeat and the other signs of life given (a result's finish time, a status file's last write); null when there is none.</summary>
    public static DateTimeOffset? LastKnownAlive(RunRecord? row, params DateTimeOffset?[] signs)
        => signs.Append(row?.LastHeartbeatAt).Where(s => s is not null).Max();

    /// <summary>The <c>finishedAt</c> a worker wrote into <c>result.json</c> (top level or under <c>timing</c>; the worker's clock), or null when absent or unreadable.</summary>
    public static DateTimeOffset? ResultFinishedAt(string? resultJson)
    {
        if (string.IsNullOrWhiteSpace(resultJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(resultJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var text = root.TryGetProperty("finishedAt", out var top) && top.ValueKind == JsonValueKind.String
                ? top.GetString()
                : root.TryGetProperty("timing", out var timing) && timing.ValueKind == JsonValueKind.Object && timing.TryGetProperty("finishedAt", out var nested) && nested.ValueKind == JsonValueKind.String
                    ? nested.GetString()
                    : null;
            return DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var at) ? at : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
