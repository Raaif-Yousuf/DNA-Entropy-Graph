namespace DnaEntropyGraph.Core.Cloud.Audit;

/// <summary>
/// How a gateway tells the audit decorator around it the ids Google gave a call, without changing any gateway signature. The decorator
/// opens a scope per call; the gateway calls <see cref="Note"/> from inside it. Outside any scope, <see cref="Note"/> does nothing.
/// </summary>
public static class CloudAuditContext
{
    private static readonly AsyncLocal<Holder?> Current = new();

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
