namespace DnaEntropyGraph.Core.Cloud;

/// <summary>How the app's clean-up of one create that landed after a cancel stopped waiting for it ended (issue #938).</summary>
public enum LateCreateState
{
    /// <summary>The create landed and the VM it made was deleted and read gone.</summary>
    Deleted,

    /// <summary>The create landed but its VM was already gone (the worker or the platform got there first), or is not this job's.</summary>
    NothingToDelete,

    /// <summary>The create failed, so it made no VM.</summary>
    CreateFailed,

    /// <summary>The VM landed and could not be confirmed deleted within the lifecycle timeout; <see cref="LateCreateOutcome.ErrorClass"/> says what failed. The VM's own maxRunDuration is the backstop.</summary>
    DeleteFailed,

    /// <summary>The create had not landed when the reap window ran out; the VM's own maxRunDuration is the backstop.</summary>
    GaveUp,
}

/// <summary>What happened to a create that was still in flight when a cancel stopped waiting. Ids and classes only: never a name, a zone or a message.</summary>
public sealed record LateCreateOutcome(string JobId, LateCreateState State, string? ErrorClass = null);
