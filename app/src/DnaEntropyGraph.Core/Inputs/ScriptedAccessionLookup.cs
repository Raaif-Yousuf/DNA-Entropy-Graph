namespace DnaEntropyGraph.Core.Inputs;

/// <summary>
/// An <see cref="IAccessionLookup"/> that never touches the network: it answers from a script. Used by tests and by developer mode
/// (<c>--fake-cloud</c>, issue #69), where nothing may reach a real service. By default every accession exists and is
/// <see cref="DefaultLength"/> bases long; <see cref="Script"/> overrides one accession (the accession alone, upper case).
/// </summary>
public sealed class ScriptedAccessionLookup : IAccessionLookup
{
    public const long DefaultLength = 29_903;

    private readonly List<(string Accession, string? Range)> _asked = [];

    /// <summary>Per accession, what to answer instead of the default.</summary>
    public Dictionary<string, AccessionLookupResult> Script { get; } = new(StringComparer.Ordinal);

    /// <summary>Every lookup made, in order.</summary>
    public IReadOnlyList<(string Accession, string? Range)> Asked => _asked;

    public Task<AccessionLookupResult> LookupAsync(string accession, string? range, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_asked)
        {
            _asked.Add((accession, range));
        }

        if (Script.TryGetValue(accession, out var scripted))
        {
            return Task.FromResult(scripted);
        }

        var length = DefaultLength;
        if (range is not null)
        {
            var parts = range.Split('-');
            length = Math.Min(long.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture), DefaultLength) - long.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture) + 1;
        }

        return Task.FromResult(AccessionLookupResult.Found(Math.Max(length, 1), accession));
    }
}
