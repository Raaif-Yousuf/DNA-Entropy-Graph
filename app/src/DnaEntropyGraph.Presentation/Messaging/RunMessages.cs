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

/// <summary>
/// Published by <c>RunStallCoordinator</c> (issue #675) when the runner says a run is slow or stuck: <paramref name="Text"/> is the notice in the user's words
/// (naming one action), or empty to clear it. <c>RunProgressViewModel</c> shows it on the run page.
/// </summary>
public sealed record RunNoticeMessage(string JobId, string Text);

/// <summary>
/// Issue #830 review M3: sent by the app when the reconciler could not look at a run or a computer because the Google account it was made under is
/// signed out. <paramref name="AccountSub"/> is that account's stable id (null when not known), never its email. <c>SignInNeededViewModel</c> turns it into the shell's "Sign in as ..." bar.
/// </summary>
public sealed record AccountSignInNeededMessage(string? AccountSub);
