using DnaEntropyGraph.Core.Abstractions;

namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// Issue #830: the shared rules for working with SETS of scopes. The installation-wide sweeps (the reconciler's idle sweep, the left-running scan) list in every scope a recent run of
/// this installation was made in plus the selected one; this is where "every scope" is computed and where one project under one account is never listed twice.
/// </summary>
public static class CloudScopes
{
    /// <summary>How far back a run row still puts its scope in a sweep (a keep-alive or a left-running computer belongs to a row this young).</summary>
    public static readonly TimeSpan LifecycleLookback = TimeSpan.FromDays(14);

    /// <summary>
    /// The distinct scopes of <paramref name="scopes"/>. A scope with no account (a legacy row) acts as the current account, which is <paramref name="selected"/>'s, so it is that account
    /// here: a null-account scope and the selected scope naming the same project are ONE scope, not two listings. Without a selection a null account stays null.
    /// </summary>
    public static IReadOnlyList<CloudScope> Dedupe(IEnumerable<CloudScope> scopes, CloudScope? selected)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        return [.. scopes
            .Where(s => !string.IsNullOrWhiteSpace(s.ProjectId))
            .Select(s => s.AccountSub is null && selected?.AccountSub is { } current ? s with { AccountSub = current } : s)
            .Distinct()];
    }

    /// <summary>
    /// The scopes to sweep: that of the latest row of every cloud run of <paramref name="installation"/> that is inside <see cref="LifecycleLookback"/> (terminal rows count: a keep-alive
    /// computer belongs to a finished run, and a live run's computer to a live one), plus <paramref name="selected"/>, with duplicates removed.
    /// </summary>
    public static IReadOnlyList<CloudScope> ForSweep(IReadOnlyList<RunRecord> allRows, string installation, DateTimeOffset now, CloudScope? selected)
    {
        ArgumentNullException.ThrowIfNull(allRows);
        var recent = allRows
            .Select(r => r.JobId)
            .Distinct()
            .Select(id => RunRowStore.LatestRecord(allRows, id)!)
            .Where(r => string.Equals(r.Target, "cloud", StringComparison.OrdinalIgnoreCase)
                && string.Equals(r.InstallationId, installation, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(r.ProjectId)
                && (JobStateMachine.IsTerminal(r.Phase) ? (r.FinishedAt ?? r.CreatedUtc) : DateTimeOffset.MaxValue) >= now - LifecycleLookback)
            .Select(RunScope.Of);
        return Dedupe(selected is null ? recent : recent.Append(selected), selected);
    }

    /// <summary>True when a live row's scope and a listed scope name the same project and, where both name one, the same account. A row with no project predates scopes and matches any.</summary>
    public static bool Matches(CloudScope row, CloudScope listed)
        => string.IsNullOrEmpty(row.ProjectId)
            || (string.Equals(row.ProjectId, listed.ProjectId, StringComparison.Ordinal)
                && (row.AccountSub is null || listed.AccountSub is null || string.Equals(row.AccountSub, listed.AccountSub, StringComparison.Ordinal)));
}
