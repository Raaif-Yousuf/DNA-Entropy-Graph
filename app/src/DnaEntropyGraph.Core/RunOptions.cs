namespace DnaEntropyGraph.Core;

/// <summary>
/// Every user-chosen option for one run (docs/superpowers/specs Appendix A
/// section 2.3). The skeleton carries the fields needed to compile the
/// projects that depend on this type; the full option set is added by the
/// issue that builds the New Run page and its ViewModel.
/// </summary>
public sealed record RunOptions
{
    public required string ModelId { get; init; }

    public required string RunTarget { get; init; }

    public string? OutputFolder { get; init; }
}
