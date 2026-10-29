namespace DnaEntropyGraph.Core.Cloud.Audit;

/// <summary>
/// How a gateway tells the audit decorator around it the ids Google gave a call, without changing any gateway signature. The decorator
/// opens a scope per call; the gateway calls <see cref="Note"/> from inside it. Outside any scope, <see cref="Note"/> does nothing.
/// </summary>
public static class CloudAuditContext
{
    private static readonly AsyncLocal<Holder?> Current = new();
    private static readonly AsyncLocal<CancellationToken?> Caller = new();

    /// <summary>
    /// Names the token the CALLER of the next audited gateway call holds. A caller that gives the gateway a linked deadline token (GatewayCalls) calls this around the call,
    /// so a cancellation of that linked token is told apart: the caller's own token cancelled is "cancelled", only the app's deadline firing is "timeout".
    /// </summary>
    public static IDisposable CallerScope(CancellationToken callerToken)
    {
        var previous = Caller.Value;
        Caller.Value = callerToken;
        return new CallerRestore(previous);
    }

    /// <summary>
    /// THE way to start an audited gateway call under an app-made deadline: hands <paramref name="call"/> the <paramref name="deadlineToken"/> (a token linked to the caller's)
    /// while naming <paramref name="callerToken"/> as the caller's own, so the audit records a deadline as "timeout" and a user cancel as "cancelled". Every place in
    /// Core that links a deadline token to a call into a gateway uses this (GatewayCalls, the reconciler), so the classification cannot be forgotten at a new site.
    /// </summary>
    public static Task<T> StartUnderDeadline<T>(CancellationToken callerToken, CancellationToken deadlineToken, Func<CancellationToken, Task<T>> call)
    {
        using (CallerScope(callerToken))
        {
            return call(deadlineToken);
        }
    }

    /// <summary>
    /// The one way to give up on a call started by <see cref="StartUnderDeadline{T}"/> (a deadline passed, or the caller cancelled while the wait unwound before the link
    /// passed it on): tells the call to stop now, and observes a fault it may still raise later, since nobody is waiting to see it. Never throws: a callback the callee
    /// registered on its token can throw, and <see cref="CancellationTokenSource.CancelAsync"/> would then raise an <see cref="AggregateException"/> (MEASURED 2026-10-29) that
    /// REPLACES the cancellation or timeout the caller's filters look for. The scope is narrow: this protects the app's OWN abandon of a call. It does not make a throwing callee
    /// callback harmless in general: when the deadline fires, the callback also runs on the timer's thread through the linked source, where nothing here can catch it
    /// (MEASURED 2026-10-29: a callback that throws on a timed-out call ended the test host with an unhandled AggregateException), and THEORY (unverified): it can surface out of the
    /// user's own Cancel() if the link relays before the wait unwinds (not seen in 13 runs).
    /// </summary>
    public static async Task StopAbandonedAsync(CancellationTokenSource deadline, Task? abandoned)
    {
        try
        {
            await deadline.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Only the class is traced (a callee's message can carry a name).
            System.Diagnostics.Trace.TraceWarning($"A cancellation callback of an abandoned cloud call threw: {ex.GetType().Name}");
        }

        if (abandoned is not null)
        {
            _ = abandoned.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    internal static CancellationToken? CurrentCaller => Caller.Value;

    private sealed class CallerRestore(CancellationToken? previous) : IDisposable
    {
        public void Dispose() => Caller.Value = previous;
    }

    /// <summary>Records the request id the call sent and/or the operation name Google answered with. A null keeps what is already noted.</summary>
    public static void Note(string? requestId, string? operationId)
    {
        var holder = Current.Value;
        if (holder is null)
        {
            return;
        }

        holder.RequestId = requestId ?? holder.RequestId;
        holder.OperationId = operationId ?? holder.OperationId;
    }

    /// <summary>
    /// The request id a call that makes up its own id (the compute insert's <c>requestId</c>) must send. When the decorator minted one before the
    /// call (so the <c>started</c> line could carry it) that one is used; otherwise a new one is made and noted, so a call that then fails
    /// still leaves its id in the audit entry. Outside any scope it is just a fresh id.
    /// </summary>
    public static string RequestIdForCall()
    {
        var holder = Current.Value;
        if (holder is null)
        {
            return Guid.NewGuid().ToString();
        }

        return holder.RequestId ??= Guid.NewGuid().ToString();
    }

    internal static Scope Begin()
    {
        var holder = new Holder();
        var previous = Current.Value;
        Current.Value = holder;
        return new Scope(holder, previous);
    }

    internal sealed class Holder
    {
        public string? RequestId { get; set; }

        public string? OperationId { get; set; }
    }

    internal readonly struct Scope(Holder holder, Holder? previous) : IDisposable
    {
        public Holder Holder { get; } = holder;

        /// <summary>Restores the enclosing call's holder (a decorated call made from inside another one), not null.</summary>
        public void Dispose() => Current.Value = previous;
    }
}
