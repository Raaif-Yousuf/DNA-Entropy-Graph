using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Runs;

namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// Issue #830 review F4: whether a scope (project and Google account) still has something that could be running, so a deliberately signed-out account is
/// not nagged about at every exit for two weeks. A scope has an OBLIGATION when one of this installation's recent cloud runs in it is not finished, or
/// finished with a computer whose end was not confirmed (<see cref="RunErrorCodes.IsVmEndUnconfirmed"/>), or finished with a keep-alive window that has not
/// expired yet (the computer is held on purpose until then). Anything else (a Completed run whose computer was stopped or deleted and verified) leaves nothing
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

    /// <summary>True when any recent run of <paramref name="installation"/> made under account <paramref name="accountSub"/> (null: a row with no recorded account) could still have a computer running.</summary>
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
                && r.AccountSub == accountSub)
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

        // A keep-alive window holds the computer on purpose until it expires, and only after a worker exit 0 (Completed or PartiallyCompleted), as the reconciler reads it.
        if (row.Phase is JobPhase.Completed or JobPhase.PartiallyCompleted && RunOptionsJson.TryDeserialize(row.OptionsJson) is { AfterTask: AfterTaskAction.KeepAlive } options)
        {
            return now < (row.FinishedAt ?? row.CreatedUtc).AddMinutes(options.KeepAliveMinutes);
        }

        return false;
    }
}
