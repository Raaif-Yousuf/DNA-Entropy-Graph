namespace DnaEntropyGraph.Core;

/// <summary>
/// The state machine driving one run, per docs/architecture.md section 4.
/// This is the skeleton's enum only - legal-transition enforcement
/// (<c>JobStateMachine</c>) is scoped to the issue that implements the
/// runner, not this one (#61 is the solution skeleton).
/// </summary>
public enum JobPhase
{
    Draft,
    Validating,
    Uploading,
    Provisioning,
    Preparing,
    Running,
    Finalizing,
    Downloading,
    Completed,
    PartiallyCompleted,
    Cancelling,
    Cancelled,
    Failed,
}
