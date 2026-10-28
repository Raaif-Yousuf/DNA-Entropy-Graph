using DnaEntropyGraph.Core.Cloud;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>
/// Signals a test awaits instead of polling a state on a real clock (#567, #647): a poll window that is too short under load fails a test that is right, and one that
/// is long enough only hides the race. The only time bound left on a wait is a generous safety net (<see cref="Safety"/>) that turns a lost signal into a failure, never a pass.
/// </summary>
internal static class RunSignals
{
    /// <summary>The longest any signal is awaited: a lost signal fails the test; a slow machine never does.</summary>
    public static readonly TimeSpan Safety = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Completes once <paramref name="jobId"/> has a registered driver. Subscribes first and checks <see cref="ActiveRuns.IsActive"/> second, so a start that
    /// happens before or after the call is seen either way.
    /// </summary>
    public static async Task WhenActiveAsync(this ActiveRuns active, string jobId)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(string id)
        {
            if (id == jobId)
            {
                started.TrySetResult();
            }
        }

        active.Started += Handler;
        try
        {
            if (active.IsActive(jobId))
            {
                started.TrySetResult();
            }

            await started.Task.WaitAsync(Safety, TestContext.Current.CancellationToken).ConfigureAwait(false);
        }
        finally
        {
            active.Started -= Handler;
        }
    }
}
