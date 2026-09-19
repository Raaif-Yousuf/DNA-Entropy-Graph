using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Contract;

namespace DnaEntropyGraph.Presentation.Messaging;

/// <summary>
/// Published by the App-layer <c>JobEngine</c> over
/// <c>CommunityToolkit.Mvvm.Messaging.IMessenger</c> so any page can react to
/// a run's phase with no direct reference back to <c>JobEngine</c>
/// (docs/architecture.md section 3: "publishes <c>RunProgressChanged</c>/
/// <c>RunPhaseChanged</c> over <c>WeakReferenceMessenger</c>"). Defined in
/// Presentation, not Core/Abstractions, because only App and Presentation
/// ever send or receive it - Cloud, Persistence and LocalEngine have no
/// reason to know this type exists.
/// </summary>
public sealed record RunPhaseChangedMessage(string JobId, JobPhase Phase);

/// <summary>
/// Published alongside a worker-shaped progress update once a real runner
/// drives one (CloudJobRunner/LocalJobRunner wiring is a follow-up issue
/// under the #61 epic, per JobEngine's own doc comment); RunProgressViewModel
/// is the intended subscriber for the live per-window narration text.
/// </summary>
public sealed record RunProgressChangedMessage(string JobId, ProgressEvent Progress);
