namespace DnaEntropyGraph.Core.Cloud;

/// <summary>What a gateway's restore of a borrowed VM's earlier labels found (issue #970).</summary>
public enum LabelRestoreState
{
    /// <summary>The VM carried the borrowing job's labels and now carries the donor's again.</summary>
    Restored,

    /// <summary>The VM no longer carries the borrowing job's id (a newer run took it, or someone relabelled it): nothing was written, so a newer owner is never clobbered.</summary>
    NewerOwner,

    /// <summary>The VM is gone: nothing to restore.</summary>
    Gone,
}

/// <summary>How the restore of a borrowed VM's earlier labels ended for one job (issue #970).</summary>
public enum DonorRestoreState
{
    Restored,
    NewerOwner,
    Gone,

    /// <summary>The VM could not be confirmed stopped within the lifecycle timeout, so its labels were left on the borrowing job (the by-name guards are the backstop).</summary>
    NotStopped,

    /// <summary>The restore call failed; <see cref="DonorRestoreOutcome.ErrorClass"/> says why. The VM is stopped and still carries the borrowing job's labels.</summary>
    Failed,
}

/// <summary>What became of the labels of a VM a run borrowed by reuse once that run failed or was cancelled. Ids and classes only: never a name, a zone or a message.</summary>
public sealed record DonorRestoreOutcome(string JobId, DonorRestoreState State, string? ErrorClass = null)
{
    /// <summary>The one token the diagnostics log records: the state, and the error class after a colon when there is one.</summary>
    public string LogClass => ErrorClass is null ? State.ToString() : $"{State}:{ErrorClass}";

    /// <summary>
    /// The sentence a failed or cancelled run's row gets about the VM it borrowed, or null when there is nothing to say (the VM is gone). Worded so no outcome reads as an all-clear it is not,
    /// and every one that needs the user names the Cloud page.
    /// </summary>
    public static string? SentenceFor(IReadOnlyList<DonorRestoreOutcome> outcomes)
    {
        var parts = new List<string>();
        foreach (var outcome in outcomes)
        {
            var part = outcome.State switch
            {
                DonorRestoreState.Restored => "The earlier VM this run borrowed was stopped and its earlier labels were restored, so the keep-alive of the run it came from still applies.",
                DonorRestoreState.NewerOwner => "The earlier VM this run borrowed now belongs to a newer run, so its labels were left alone.",
                DonorRestoreState.NotStopped => "The earlier VM this run borrowed could not be confirmed stopped, so its labels were not restored. Stop it from the Cloud page.",
                DonorRestoreState.Failed => $"The earlier VM this run borrowed was stopped, but its earlier labels could not be restored ({outcome.ErrorClass ?? RunErrorCodes.Other}), so the Cloud page lists it under this run. Check the Cloud page.",
                _ => null,
            };
            if (part is not null && !parts.Contains(part))
            {
                parts.Add(part);
            }
        }

        return parts.Count == 0 ? null : string.Join(' ', parts);
    }
}
