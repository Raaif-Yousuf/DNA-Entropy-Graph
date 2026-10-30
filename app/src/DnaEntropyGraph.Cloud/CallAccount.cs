using DnaEntropyGraph.Core.Cloud;

namespace DnaEntropyGraph.Cloud;

/// <summary>
/// Issue #830: the account the Google request being built right now is authorised as. <see cref="CloudCallPipeline"/> sets it around every
/// gateway call from the call's <c>CloudScope.AccountSub</c> (and restores it after), and the bearer handlers in <c>GoogleRestClient</c> read it
/// when they fetch the token, so a run acts as ITS account whichever account is current. Null means "the current account", which only the
/// calls that are not scoped to a run (the wizard's catalogue, billing and service steps) rely on.
/// DECISION (agent-made, reversible): an ambient value set by the one pipeline instead of a per-request <c>Credential</c> on every request
/// site (hundreds, across seven gateways): the same guarantee (one account per request, never re-resolved at send time) with one place to get wrong.
/// </summary>
public static class CallAccount
{
    private static readonly AsyncLocal<string?> Value = new();

    /// <summary>
    /// Every scoped gateway method opens with <c>using var _ = CallAccount.Use(scope);</c>: the account is then fixed for that method and everything
    /// it awaits (the pipeline's retries and its 401 refresh included), and restored on exit.
    /// </summary>
    public static IDisposable Use(CloudScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var previous = Value.Value;
        Value.Value = scope.AccountSub;
        return new Restore(previous);
    }

    private sealed class Restore(string? previous) : IDisposable
    {
        public void Dispose() => Value.Value = previous;
    }

    /// <summary>The account sub the current call is made as; null for the current account.</summary>
    public static string? Current
    {
        get => Value.Value;
        internal set => Value.Value = value;
    }
}
