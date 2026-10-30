namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// Issue #830: the project AND the Google account a cloud call acts as. A run, a keep-alive row or a Cloud page listing is tied to the
/// scope it was made in, never to whatever project or account is selected when a later call happens. Every gateway method that acts on a
/// project or on a run's resources takes one explicitly, so no call compiles without a scope.
/// <paramref name="AccountSub"/> is the stable Google account id (never the email); null only for a row written before accounts were
/// recorded, which is backfilled once (see the <c>Runs.AccountSub</c> migration), never re-resolved per call.
/// </summary>
public sealed record CloudScope(string ProjectId, string? AccountSub = null);
