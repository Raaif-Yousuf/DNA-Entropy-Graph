namespace DnaEntropyGraph.Core.Cloud;

/// <summary>How the app's clean-up of one create that landed after a cancel stopped waiting for it ended (issue #938).</summary>
public enum LateCreateState
{
    /// <summary>The create landed and the VM it made was deleted and read gone.</summary>
    Deleted,

    /// <summary>The create landed but its VM was already gone (the worker or the platform got there first), or is not this job's.</summary>
    NothingToDelete,

    /// <summary>The create was refused, or ended without a VM, and a look by name after it ended (and the grace for an accepted insert to show) found none.</summary>
    CreateFailed,

    /// <summary>The VM landed and could not be confirmed deleted within the lifecycle timeout; <see cref="LateCreateOutcome.ErrorClass"/> says what failed. The VM's own maxRunDuration is the backstop.</summary>
    DeleteFailed,

    /// <summary>No look ever saw a VM, and the looks and the delete by name kept failing: the app cannot say whether the create made one. <see cref="LateCreateOutcome.ErrorClass"/> says what failed.</summary>
    CouldNotConfirm,

    /// <summary>The create had not landed when the reap window ran out; the VM's own maxRunDuration is the backstop.</summary>
    GaveUp,

    /// <summary>A newer attempt of the same job created, adopted or reused a VM under the same name in the same zone: it is that attempt's now, and this clean-up left it alone.</summary>
    Superseded,

    /// <summary>Round 4 M3: a reuse still in flight at the cancel landed (the earlier run's VM started) and was stopped again. Never deleted: it is another run's VM.</summary>
    ReuseStopped,

    /// <summary>Round 4 M3: a reuse landed and the VM could not be confirmed stopped within the lifecycle timeout; <see cref="LateCreateOutcome.ErrorClass"/> says what failed.</summary>
    ReuseStopFailed,
}

/// <summary>What happened to a create that was still in flight when a cancel stopped waiting. Ids and classes only: never a name, a zone or a message.</summary>
public sealed record LateCreateOutcome(string JobId, LateCreateState State, string? ErrorClass = null)
{
    /// <summary>The one token the diagnostics log records for this outcome: the state, and the error class after a colon when there is one. Never a name, a zone or a message.</summary>
    public string LogClass => ErrorClass is null ? State.ToString() : $"{State}:{ErrorClass}";
}
