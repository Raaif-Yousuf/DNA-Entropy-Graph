namespace DnaEntropyGraph.Core.Inputs;

/// <summary>
/// What to do with an IUPAC ambiguity code (<c>N</c>, <c>R</c>, <c>Y</c>, ...) - one of the
/// 11 single-letter codes for "more than one possible base here" that real GenBank/FASTA
/// files routinely contain, but which are NOT one of the four bases the predictor's
/// <c>(L, 4)</c> contract is built on (issue #249). Mirrors
/// <c>worker/src/dna_entropy/config.py</c>'s <c>AmbiguityPolicy</c> value-for-value so a
/// <see cref="RunOptions" /> choice serialises to the exact string the manifest expects.
/// See docs/science_and_formats.md for what each policy does to the entropy numbers at an
/// ambiguous position.
/// </summary>
public enum AmbiguityPolicy
{
    /// <summary>Feed the original code straight to the predictor, unmodified. Wire value: "keep".</summary>
    Keep,

    /// <summary>Normalize every code to a single canonical N before predicting. Wire value: "mask".</summary>
    Mask,

    /// <summary>Refuse the input outright if it contains any ambiguity code. Wire value: "error".</summary>
    Error,
}

/// <summary>Wire-format (manifest.json) string conversions for <see cref="AmbiguityPolicy" />.</summary>
public static class AmbiguityPolicyExtensions
{
    public static string ToWireValue(this AmbiguityPolicy policy) => policy switch
    {
        AmbiguityPolicy.Keep => "keep",
        AmbiguityPolicy.Mask => "mask",
        AmbiguityPolicy.Error => "error",
        _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, "Unknown AmbiguityPolicy"),
    };
}
