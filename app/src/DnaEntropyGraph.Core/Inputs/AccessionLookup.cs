namespace DnaEntropyGraph.Core.Inputs;

/// <summary>Why the record for an accession cannot be fetched (issue #684). One copy line, with one action, per value, in Resources.resw.</summary>
public enum AccessionLookupProblem
{
    /// <summary>NCBI has no such record (or answered with a different one).</summary>
    NotFound,

    /// <summary>The range starts after the record ends.</summary>
    RangeOutsideRecord,

    /// <summary>The record, or the part asked for, is bigger than the worker reads.</summary>
    TooLarge,
    RateLimited,
    NcbiTrouble,
    TimedOut,

    /// <summary>NCBI could not be reached from this PC, or refused the request.</summary>
    Unreachable,
}

/// <summary>What NCBI says about a record: <see cref="Length"/> is the bases the run would fetch (the range, clipped to the record).</summary>
public sealed record AccessionLookupResult(long Length, string Resolved, AccessionLookupProblem? Problem)
{
    public bool IsValid => Problem is null;

    public static AccessionLookupResult Found(long length, string resolved) => new(length, resolved, null);

    public static AccessionLookupResult Failed(AccessionLookupProblem problem) => new(0, string.Empty, problem);
}

/// <summary>
/// Asks, from this PC and before any cloud resource exists, whether an accession's record is there and fits the worker (Hard Rule 2:
/// nobody pays for a VM to learn the record does not exist). The accession and range are already checked by <see cref="AccessionInput"/>.
/// </summary>
public interface IAccessionLookup
{
    Task<AccessionLookupResult> LookupAsync(string accession, string? range, CancellationToken cancellationToken);
}

/// <summary>The worker's own limits on a fetched record, copied from <c>readers/entrez.py</c> (a test reads the file and fails on drift).</summary>
public static class AccessionLimits
{
    /// <summary><c>_MAX_BODY_BYTES</c>: the GenBank text the worker reads from NCBI.</summary>
    public const long MaxBodyBytes = 64L * 1024 * 1024;

    /// <summary>
    /// True when a record of <paramref name="bases"/> bases is certainly over <see cref="MaxBodyBytes"/> as GenBank text. A GenBank
    /// sequence line carries 60 bases in 75 bytes (1.25 a base) and the features only add to that, so this never refuses a record that fits.
    /// </summary>
    public static bool IsTooLarge(long bases) => bases * 5 > MaxBodyBytes * 4;
}
