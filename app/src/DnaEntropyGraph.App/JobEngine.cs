using System.Collections.Concurrent;
using CommunityToolkit.Mvvm.Messaging;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Contract;
using DnaEntropyGraph.Presentation.Messaging;
using DnaEntropyGraph.Presentation.Services;

namespace DnaEntropyGraph.App;

/// <summary>
/// Owns the active runners and publishes <see cref="RunPhaseChangedMessage"/>
/// over <see cref="IMessenger"/> (docs/architecture.md section 3) so any page
/// - <c>ShellViewModel</c>'s active-run badge today, <c>RunProgressViewModel</c>'s
/// stage timeline once it subscribes too - can react with no direct
/// reference back to this class. The skeleton announces
/// <see cref="JobPhase.Validating"/> on start and a terminal phase on
/// cancel; driving every intermediate phase is <c>CloudJobRunner</c>'s and
/// <c>LocalJobRunner</c>'s own follow-up issue under this epic, not this one.
/// </summary>
public sealed class JobEngine : IJobEngine, IRunVmActions
{
    private readonly ConcurrentDictionary<string, RunOptions> _activeRuns = new();
    private readonly IMessenger _messenger;

    public JobEngine(IMessenger messenger)
    {
        _messenger = messenger;
    }

    public IReadOnlyList<WorkerResult> CompletedRuns { get; } = Array.Empty<WorkerResult>();

    public Task<string> StartRunAsync(RunOptions options, CancellationToken cancellationToken)
    {
        var jobId = Guid.NewGuid().ToString("n");
        _activeRuns[jobId] = options;
        _messenger.Send(new RunPhaseChangedMessage(jobId, JobPhase.Validating));
        return Task.FromResult(jobId);
    }

    public Task CancelRunAsync(string jobId, CancellationToken cancellationToken)
    {
        var wasActive = _activeRuns.TryRemove(jobId, out _);
        if (wasActive)
        {
            _messenger.Send(new RunPhaseChangedMessage(jobId, JobPhase.Cancelled));
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// THEORY (unverified) / documented placeholder: no CloudJobRunner
    /// exists yet to hold a real VM reference this class could stop
    /// (see this class's own doc comment). The real Compute API stop call
    /// belongs in DnaEntropyGraph.Cloud behind an interface in Core/Cloud
    /// (Hard Rule 7); until it lands, "stop the VM" degrades to the same
    /// cancellation effect as <see cref="CancelRunAsync"/> rather than
    /// silently doing nothing, so the button is real and testable today
    /// without claiming a real VM was ever stopped.
    /// </summary>
    public Task StopVmAsync(string jobId, CancellationToken cancellationToken) => CancelRunAsync(jobId, cancellationToken);

    /// <summary>See <see cref="StopVmAsync"/> - same placeholder, same reasoning, for "Delete VM now".</summary>
    public Task DeleteVmAsync(string jobId, CancellationToken cancellationToken) => CancelRunAsync(jobId, cancellationToken);
}
