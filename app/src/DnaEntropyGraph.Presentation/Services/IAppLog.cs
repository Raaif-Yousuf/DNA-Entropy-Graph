namespace DnaEntropyGraph.Presentation.Services;

/// <summary>
/// Where a ViewModel records that something failed (CLAUDE.md "Logs": ids, states and error classes, never the sequence,
/// the file name or the email). It takes an area and a class name on purpose, not an exception: an exception message can
/// carry a path.
/// </summary>
public interface IAppLog
{
    /// <param name="area">Where it happened, for example <c>NewRun.StartRun</c>.</param>
    /// <param name="errorClass">The exception type, plus the cloud error kind when there is one, for example <c>CloudOperationException:Quota</c>.</param>
    void Error(string area, string errorClass);
}
