namespace DnaEntropyGraph.Presentation.Services;

/// <summary>
/// The VM-scoped actions available while a run's phase implies a rented
/// computer exists (docs/ui_conventions.md section 7's Provisioning /
/// Preparing / Running / Finalizing rows: "Stop VM now" / "Delete VM now").
///
/// Deliberately separate from <c>IJobEngine</c> (Core/Abstractions, owned
/// elsewhere in this session's split): this is a narrower, run-progress-
/// page-specific surface, not a change to that interface's contract. The
/// real implementation - an actual Compute API stop/delete - belongs in
/// DnaEntropyGraph.Cloud behind an interface in Core/Cloud (Hard Rule 7),
/// once CloudJobRunner exists; see this session's report, "Needed outside
/// my lane". Today's App-layer implementation is a documented placeholder.
/// </summary>
public interface IRunVmActions
{
    Task StopVmAsync(string jobId, CancellationToken cancellationToken);

    Task DeleteVmAsync(string jobId, CancellationToken cancellationToken);
}
