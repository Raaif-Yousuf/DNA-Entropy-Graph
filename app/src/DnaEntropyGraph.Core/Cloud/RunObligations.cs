using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Runs;

namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// Issue #830 review F4: whether a scope (project and Google account) still has something that could be running, so a deliberately signed-out account is
/// not nagged about at every exit for two weeks. A scope has an OBLIGATION when one of this installation's recent cloud runs in it is not finished, or
/// finished with a computer whose end was not confirmed (<see cref="RunErrorCodes.IsVmEndUnconfirmed"/>), or finished with a keep-alive window (the computer is held on purpose,
/// and stays up after the window until something stops it: review M1 round 3). Anything else (a Completed run whose computer was stopped or deleted and verified) leaves nothing
/// to look after, so a sign-in prompt for that account has nothing to protect and is only logged.
/// </summary>
public static class RunObligations
{
    /// <summary>True when <paramref name="scope"/> has a recent run of <paramref name="installation"/> that could still have a computer running.</summary>
    public static bool Exist(IReadOnlyList<RunRecord> allRows, CloudScope scope, string installation, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(allRows);
        ArgumentNullException.ThrowIfNull(scope);
        return allRows
            .Select(r => r.JobId)
            .Distinct()
            .Select(id => RunRowStore.LatestRecord(allRows, id)!)
            .Where(r => string.Equals(r.Target, "cloud", StringComparison.OrdinalIgnoreCase)
                && string.Equals(r.InstallationId, installation, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(r.ProjectId)
                && CloudScopes.Matches(RunScope.Of(r), scope))
            .Any(r => HasObligation(r, now));
    }

    /// <summary>
    /// True when any recent run of <paramref name="installation"/> made under account <paramref name="accountSub"/> could still have a computer running. Round 4 L-1: an outcome that names NO account (null) matches only rows that record no account,
    /// never every account's rows (the wildcard below is for a ROW with no account, not for a caller with none). Review M2 (round 3): a named account is matched by the SAME rule as
    /// <see cref="Exist"/> and every other scope comparison (<see cref="CloudScopes.Matches"/>): a row with no recorded account acts as the current account, so it belongs to whichever account the caller names
    /// (the pass names the one that was current when it could not act).
    /// </summary>
    public static bool ExistForAccount(IReadOnlyList<RunRecord> allRows, string? accountSub, string installation, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(allRows);
        return allRows
            .Select(r => r.JobId)
            .Distinct()
            .Select(id => RunRowStore.LatestRecord(allRows, id)!)
            .Where(r => string.Equals(r.Target, "cloud", StringComparison.OrdinalIgnoreCase)
                && string.Equals(r.InstallationId, installation, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(r.ProjectId)
                && (accountSub is null ? r.AccountSub is null : CloudScopes.Matches(RunScope.Of(r), new CloudScope(r.ProjectId!, accountSub))))
            .Any(r => HasObligation(r, now));
    }

    /// <summary>True when <paramref name="row"/> (the latest record of its job) can still have a computer running at <paramref name="now"/>.</summary>
    public static bool HasObligation(RunRecord row, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!JobStateMachine.IsTerminal(row.Phase))
        {
            return true;
        }

        if ((row.FinishedAt ?? row.CreatedUtc) < now - CloudScopes.LifecycleLookback)
        {
            return false;
        }

        if (RunErrorCodes.IsVmEndUnconfirmed(row.ErrorCode))
        {
            return true;
        }

        // Review M1 (round 3): a keep-alive window holds the computer on purpose, and its expiry does not END anything: the computer keeps running until the reconciler (or the person) stops it,
        // and a signed-out account can neither. The row records no "confirmed ended" state of its own (a confirmed-end column was tried in #830 rounds 4 and 5 and dropped: a stale confirmation survives a resume, and a by-name 404 cannot rule out a pending late create), so the smallest truthful signal is the one this method
        // is only asked about at its two call sites: the account could NOT be reached just now (a scope the scan could not read, or an account the pass reported as signed out). For such a row the
        // keep-alive obligation therefore stands from the first minute until the row leaves the lifecycle lookback above, where no sweep looks at it any more. This is a UX nag at worst and never a billing risk. Only worker exit 0 holds the computer
        // for a window (Completed or PartiallyCompleted, as the reconciler reads it); every other finished state applied afterKeepAlive at once.
        return row.Phase is JobPhase.Completed or JobPhase.PartiallyCompleted
            && RunOptionsJson.TryDeserialize(row.OptionsJson) is { AfterTask: AfterTaskAction.KeepAlive };
    }
}
