using System.Globalization;
using System.Text.RegularExpressions;

namespace DnaEntropyGraph.Core.Inputs;

/// <summary>What is wrong with a typed NCBI accession or range (issue #684).</summary>
public enum AccessionProblem
{
    NotAnAccession,
    RangeIncomplete,
    RangeInvalid,
}

/// <summary>The outcome of <see cref="AccessionInput.Check"/>.</summary>
/// <param name="Accession">The upper-case accession the worker will be asked for; empty when there is a problem.</param>
/// <param name="Range">The range as the manifest spells it (<c>start-end</c>), or null for the whole record.</param>
public sealed record AccessionCheck(bool IsValid, string Accession, string? Range, AccessionProblem? Problem);

/// <summary>
/// Checks an NCBI accession on this PC before any cloud resource exists (Hard Rule 2). The pattern is the worker's own
/// (<c>readers/entrez.py</c> <c>_ACCESSION</c>) and, like it, ASCII only: <c>\d</c> alone would accept digits of other scripts, and
/// upper-casing some non-ASCII letters yields ASCII ones, so a non-ASCII text is refused before it is changed.
/// </summary>
public static partial class AccessionInput
{
    // [0-9], never \d: .NET's \d matches every Unicode decimal digit. \z, never $: $ also matches before a final newline.
    // Public so a test can read it: it must equal the worker's pattern (a guard reads entrez.py) and refuse a final newline and non-ASCII digits.
    public const string PatternText = @"^[A-Za-z]{1,6}_?[A-Za-z]{0,6}[0-9]{5,10}(?:\.[0-9]{1,3})?\z";

    [GeneratedRegex(PatternText, RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    public static AccessionCheck Check(string? accession, string? rangeStart, string? rangeEnd)
    {
        var raw = (accession ?? string.Empty).Trim();
        if (!raw.All(char.IsAscii) || !Pattern().IsMatch(raw))
        {
            return Refused(AccessionProblem.NotAnAccession);
        }

        var upper = raw.ToUpperInvariant();
        var start = (rangeStart ?? string.Empty).Trim();
        var end = (rangeEnd ?? string.Empty).Trim();
        if (start.Length == 0 && end.Length == 0)
        {
            return new AccessionCheck(true, upper, null, null);
        }

        if (start.Length == 0 || end.Length == 0)
        {
            return Refused(AccessionProblem.RangeIncomplete);
        }

        if (!TryPositive(start, out var from) || !TryPositive(end, out var to) || from > to)
        {
            return Refused(AccessionProblem.RangeInvalid);
        }

        return new AccessionCheck(true, upper, from.ToString(CultureInfo.InvariantCulture) + "-" + to.ToString(CultureInfo.InvariantCulture), null);
    }

    /// <summary>Checks an accession with a range already spelled as the manifest has it (<c>start-end</c>); null or empty means the whole record.</summary>
    public static AccessionCheck Check(string? accession, string? range)
    {
        if (string.IsNullOrWhiteSpace(range))
        {
            return Check(accession, null, null);
        }

        var parts = range.Split('-');
        return parts.Length == 2 ? Check(accession, parts[0], parts[1]) : Check(accession, "x", "x");
    }

    private static AccessionCheck Refused(AccessionProblem problem) => new(false, string.Empty, null, problem);

    private static bool TryPositive(string text, out long value)
    {
        value = 0;
        return text.All(c => c is >= '0' and <= '9') && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= 1;
    }
}
