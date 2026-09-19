namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// Issue #58's legal-transition table over the <see cref="JobPhase"/> enum
/// (skeleton-only per its own doc comment - this is the issue that fills it
/// in). A phase change <see cref="CloudJobRunner"/> did not itself request
/// through this table can never reach <c>IRunRepository</c> - see
/// <see cref="EnsureLegalTransition"/>.
/// </summary>
public static class JobStateMachine
{
    /// <summary>
    /// The happy-path order used only to make a resumed run's already-passed
    /// phases into no-ops instead of illegal backward transitions (see
    /// <see cref="CloudJobRunner"/>'s crash-and-resume handling). This is
    /// deliberately a separate concept from <see cref="LegalTransitions"/>:
    /// the legal-transition table says what may happen NEXT from a given
    /// phase; this list says what already-visited phases look like when a
    /// second runner instance re-walks the same job from the start.
    /// </summary>
    public static readonly IReadOnlyList<JobPhase> HappyPathOrder =
    [
        JobPhase.Draft,
        JobPhase.Validating,
        JobPhase.Uploading,
        JobPhase.Provisioning,
        JobPhase.Preparing,
        JobPhase.Running,
        JobPhase.Finalizing,
        JobPhase.Downloading,
        JobPhase.Completed,
    ];

    private static readonly IReadOnlyDictionary<JobPhase, JobPhase[]> LegalTransitions = new Dictionary<JobPhase, JobPhase[]>
    {
        [JobPhase.Draft] = [JobPhase.Validating, JobPhase.Cancelled, JobPhase.Failed],
        [JobPhase.Validating] = [JobPhase.Uploading, JobPhase.Failed, JobPhase.Cancelling],
        [JobPhase.Uploading] = [JobPhase.Provisioning, JobPhase.Failed, JobPhase.Cancelling],
        [JobPhase.Provisioning] = [JobPhase.Preparing, JobPhase.Failed, JobPhase.Cancelling],
        [JobPhase.Preparing] = [JobPhase.Running, JobPhase.Failed, JobPhase.Cancelling],
        [JobPhase.Running] = [JobPhase.Finalizing, JobPhase.Failed, JobPhase.Cancelling],
        [JobPhase.Finalizing] = [JobPhase.Downloading, JobPhase.Failed, JobPhase.Cancelling],
        [JobPhase.Downloading] = [JobPhase.Completed, JobPhase.PartiallyCompleted, JobPhase.Failed, JobPhase.Cancelling],
        [JobPhase.Cancelling] = [JobPhase.Cancelled, JobPhase.Failed],
        [JobPhase.Completed] = [],
        [JobPhase.PartiallyCompleted] = [],
        [JobPhase.Cancelled] = [],
        [JobPhase.Failed] = [],
    };

    public static bool IsLegalTransition(JobPhase from, JobPhase to)
        => LegalTransitions.TryGetValue(from, out var allowed) && Array.IndexOf(allowed, to) >= 0;

    /// <summary>The four phases a run never leaves once entered - <see cref="CloudJobRunner"/>'s resume path returns immediately for these instead of re-running anything.</summary>
    public static bool IsTerminal(JobPhase phase)
        => phase is JobPhase.Completed or JobPhase.PartiallyCompleted or JobPhase.Cancelled or JobPhase.Failed;

    /// <summary>Throws naming both phases when <paramref name="to"/> is not a legal next phase from <paramref name="from"/>.</summary>
    public static void EnsureLegalTransition(JobPhase from, JobPhase to)
    {
        if (!IsLegalTransition(from, to))
        {
            throw new InvalidOperationException($"Illegal job phase transition: '{from}' -> '{to}'.");
        }
    }

    /// <summary>
    /// True when <paramref name="target"/> is a happy-path phase a run
    /// already at <paramref name="current"/> has already passed - the
    /// resume-safety check <see cref="CloudJobRunner"/> uses so re-walking
    /// a job from the start after a crash skips already-completed steps
    /// instead of attempting an illegal backward transition.
    /// </summary>
    public static bool HasAlreadyPassed(JobPhase current, JobPhase target)
    {
        var currentIndex = HappyPathOrder.IndexOf(current);
        var targetIndex = HappyPathOrder.IndexOf(target);
        return currentIndex >= 0 && targetIndex >= 0 && currentIndex > targetIndex;
    }
}

internal static class ReadOnlyListExtensions
{
    public static int IndexOf<T>(this IReadOnlyList<T> list, T value)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (EqualityComparer<T>.Default.Equals(list[i], value))
            {
                return i;
            }
        }

        return -1;
    }
}
