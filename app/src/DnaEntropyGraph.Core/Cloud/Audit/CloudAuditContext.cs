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

    internal static Scope Begin()
    {
        var holder = new Holder();
        Current.Value = holder;
        return new Scope(holder);
    }

    internal sealed class Holder
    {
        public string? RequestId { get; set; }

        public string? OperationId { get; set; }
    }

    internal readonly struct Scope(Holder holder) : IDisposable
    {
        public Holder Holder { get; } = holder;

        public void Dispose() => Current.Value = null;
    }
}
