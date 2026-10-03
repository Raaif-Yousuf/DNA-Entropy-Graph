namespace DnaEntropyGraph.Core.Abstractions;

/// <summary>
/// Where the app records that something it did not expect happened, so a diagnostics zip can show it (CLAUDE.md "Logs": ids, states and
/// error classes, never the sequence, the file name or the email). Deliberately this small until Serilog is wired in (a later issue):
/// the caller passes the job id and the exception CLASS, and cannot pass a message that might carry a file name.
/// </summary>
public interface IDiagnosticsLog
{
    /// <param name="source">The component, such as <c>reconciler</c>.</param>
    /// <param name="jobId">The run the error belongs to, or null when it belongs to no run.</param>
    /// <param name="errorClass">The exception's type name.</param>
    void Warning(string source, string? jobId, string errorClass);
}

/// <summary>Records nothing. The default where no log is supplied (tests, and any caller that has not been given one).</summary>
public sealed class NullDiagnosticsLog : IDiagnosticsLog
{
    public static readonly NullDiagnosticsLog Instance = new();

    public void Warning(string source, string? jobId, string errorClass)
    {
    }
}
