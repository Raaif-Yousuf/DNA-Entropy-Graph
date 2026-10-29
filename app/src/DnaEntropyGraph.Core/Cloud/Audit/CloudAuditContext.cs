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
