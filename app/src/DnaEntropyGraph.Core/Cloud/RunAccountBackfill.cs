using DnaEntropyGraph.Core.Abstractions;

namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// Issue #830 review M1: a run row written before <c>Runs.AccountSub</c> existed has no account, and nothing recorded which account made it. The design
/// is that such a row belongs to the account that is current at the first launch after the upgrade, fixed once and never re-resolved per call. So the
/// first launch with an account signed in sets every NULL cloud row to that account's sub (one UPDATE) and writes a marker so it never runs again.
/// With nobody signed in the rows stay NULL (they mean "the current account") and the next launch tries again. The sub is never logged.
/// </summary>
public sealed class RunAccountBackfill(IRunRepository runs, IGcpAccount account, ISettingsStore settings)
{
    /// <summary>The settings key that records the one-time backfill as done.</summary>
    public const string MarkerKey = "run_account_backfill_done";

    /// <summary>Returns the number of rows given an account (0 when it already ran, when nobody is signed in, or when no row needed one).</summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        if (settings.GetString(MarkerKey) == "1")
        {
            return 0;
        }

        var sub = account.CurrentAccount?.Sub;
        if (string.IsNullOrEmpty(sub))
        {
            return 0;
        }

        var changed = await runs.AssignAccountToUnassignedRunsAsync(sub, cancellationToken).ConfigureAwait(false);
        settings.SetString(MarkerKey, "1");
        return changed;
    }
}
