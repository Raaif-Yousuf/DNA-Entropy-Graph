using DnaEntropyGraph.Core.Cloud;
using Microsoft.Extensions.DependencyInjection;

namespace DnaEntropyGraph.App.Startup;

/// <summary>
/// What the app does in the background once it has launched. <c>App.OnLaunched</c> makes one call to <see cref="BeginAsync"/> and
/// nothing else that branches (Hard Rule 8); Guards.Tests calls the same method on the same production container, which is what
/// proves the reconciler is invoked and not merely registered (issue #59).
/// </summary>
public static class AppStartup
{
    /// <summary>
    /// Reattaches every run a killed or closed app left in a non-terminal phase and enforces the lifecycle of every finished run's VM
    /// (<see cref="JobReconciler.ReconcileAsync"/>); the same pass runs again when the connection returns (<see cref="ReconcileOnReconnect"/>).
    /// The returned task ends when every reattached run has ended; the app does not await it, so launch is never held up by a run that takes minutes.
    /// Never faults: a failure here must not take the app down, and the rows it could not judge are looked at again next launch.
    /// </summary>
    public static async Task BeginAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        try
        {
            // The pass runs here, not through ReconcileOnReconnect, so the reconnect probe is started by hand once the pass has judged every run:
            // a launch with no network leaves Deferred rows, and nothing else would look at them again until the next launch (issue #559).
            // In a finally: a pass that throws (the run table timed out) has recorded itself as deferred, and the probe must still look again.
            Task reattached;
            try
            {
                reattached = await services.GetRequiredService<JobReconciler>().BeginReconcileAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                services.GetRequiredService<ReconcileOnReconnect>().StartProbeIfDeferred();
            }

            await reattached.ConfigureAwait(false);

            // A cancel finished at launch can end Deferred after the pass ended: the same probe check, once the runs have ended.
            services.GetRequiredService<ReconcileOnReconnect>().StartProbeIfDeferred();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The app has no logger of its own yet (Serilog is a later issue); the run rows are the record, and an unjudged
            // row stays non-terminal so the next launch reattaches it.
        }
    }
}
