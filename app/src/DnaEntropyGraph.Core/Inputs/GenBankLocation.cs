using System.Numerics;
using System.Text.RegularExpressions;

namespace DnaEntropyGraph.Core.Inputs;

/// <summary>What Biopython does with one feature's location string (issues #543 and #788).</summary>
internal enum LocationVerdict
{
    /// <summary>Read into a location (or an exception shape this port does not model; the worker is then the judge).</summary>
    Readable,

    /// <summary><c>LocationParserError</c>: Biopython warns "setting feature location to None" and the worker drops the feature with a notice.</summary>
    Dropped,

    /// <summary>A plain <c>ValueError</c> or <c>AssertionError</c> escapes the parser, so the worker refuses the whole file.</summary>
    Refused,
}

/// <summary>The verdict, and for <see cref="LocationVerdict.Dropped" /> the text Biopython quotes in its warning (what the worker's notice shows).</summary>
internal readonly record struct LocationReading(LocationVerdict Verdict, string QuotedText = "");

/// <summary>
/// C# port of the part of Biopython's location parsing (<c>Bio.GenBank._FeatureConsumer.location</c>, <c>Bio.SeqFeature.Location.fromstring</c>,
/// <c>SimpleLocation.fromstring</c> and <c>Position.fromstring</c>, Biopython 1.88) that decides what becomes of a feature: read, dropped
/// with a notice, or the file refused. Only the verdict is ported, never a location object. It is checked row by row against
/// Biopython's own verdicts in <c>GenBankLocationMeasured</c> (MEASURED 2026-10-16), and the shapes the worker's
/// <c>test_genbank_dropped_features.py</c> measured are among them.
/// </summary>
internal static class GenBankLocation
{
    private const string Reference = @"(?:[a-zA-Z][a-zA-Z0-9_\.\|]*[a-zA-Z0-9]?\:)";
    private const string OneOfPosition = @"one\-of\(\d+[,\d+]+\)";
    private const string OneOfLocation = @"[<>]?(?:\d+|" + OneOfPosition + @")\.\.[<>]?(?:\d+|" + OneOfPosition + ")";
    private const string WithinPosition = @"\(\d+\.\d+\)";
    private const string Solo = @"[<>]?\d+";

    // Bio.SeqFeature._split: the matched groups, in order, are what `_split(text)[1::2]` returns.
    private static readonly Regex AnyLocation = new(
        "(" + Reference + "?" + OneOfLocation + @"|complement\(" + OneOfLocation + @"\)|[^,]+|complement\([^,]+\))",
        RegexOptions.CultureInvariant);

    // Bio.SeqFeature._re_location_category, used with Python's re.match (anchored at the start only; "^" and "$" sit on the
    // first and last alternative only, which is why "1..5abc" matches its pair prefix and then trips the assert).
    private static readonly Regex Category = new(
        @"\G(?:^(?<pair>[<>]?-?\d+\.\.[<>]?-?\d+)"
        + @"|(?<between>\d+\^\d+)"
        + @"|(?<within>([<>]?\d+|" + WithinPosition + @")\.\.([<>]?\d+|" + WithinPosition + "))"
        + @"|(?<oneof>([<>]?\d+|" + OneOfPosition + @")\.\.([<>]?\d+|" + OneOfPosition + "))"
        + @"|(?<bond>bond\(" + Solo + @"\))"
        + @"|(?<solo>" + Solo + "$))",
        RegexOptions.CultureInvariant);

    private static readonly string[] CategoryOrder = ["pair", "between", "within", "oneof", "bond", "solo"];
    private static readonly Regex WithinPositionRe = new(@"\G\((\d+)\.(\d+)\)", RegexOptions.CultureInvariant);
    private static readonly Regex OneOfPositionRe = new(@"\Gone\-of\((\d+[,\d+]+)\)", RegexOptions.CultureInvariant);
    private static readonly Regex QuotedText = new("'(.*)'", RegexOptions.CultureInvariant);

    private sealed class LocationParserError(string message) : Exception(message);

    /// <summary>A plain Python exception that is not a <c>LocationParserError</c>: the record's parse raises and the worker refuses the file.</summary>
    private sealed class PythonError(string message) : Exception(message);

    /// <summary>
    /// What Biopython does with <paramref name="rawLocation" /> (the feature's location, lines joined) on a record whose LOCUS line gives
    /// <paramref name="length" /> (null when it gives none) and <paramref name="circular" />.
    /// </summary>
    public static LocationReading Read(string rawLocation, long? length, bool circular)
    {
        // _clean_location: "".join(location_string.split())
        var line = string.Concat(rawLocation.Where(c => !PythonText.IsSpace(c)));
        if (line.Contains("replace", StringComparison.Ordinal))
        {
            // Older records: replace(266,"c"); keep only the number.
            line = PythonText.Slice(line, 8, line.IndexOf(',', StringComparison.Ordinal));
        }

        try
        {
            FromString(line, length, circular);
            return new LocationReading(LocationVerdict.Readable);
        }
        catch (LocationParserError e)
        {
            var quoted = QuotedText.Match($"{e.Message}; setting feature location to None.");
            return new LocationReading(LocationVerdict.Dropped, quoted.Success ? quoted.Groups[1].Value : string.Empty);
        }
        catch (PythonError)
        {
            return new LocationReading(LocationVerdict.Refused);
        }
    }

    // Location.fromstring. Returns nothing: a location that comes back is Readable.
    private static void FromString(string text, long? length, bool circular)
    {
        var complement = false;
        if (text.StartsWith("complement(", StringComparison.Ordinal))
        {
            if (text[^1] != ')')
            {
                throw new PythonError($"closing bracket missing in '{text}'");
            }

            text = PythonText.Slice(text, 11, text.Length - 1);
            complement = true;
        }

        var prefix = text.StartsWith("join(", StringComparison.Ordinal) ? 5
            : text.StartsWith("order(", StringComparison.Ordinal) ? 6
            : text.StartsWith("bond(", StringComparison.Ordinal) ? 5
            : 0;
        if (prefix == 0)
        {
            SimpleFromString(text, length, circular);
            return;
        }

        var parts = AnyLocation.Matches(PythonText.Slice(text, prefix, text.Length - 1)).Select(m => m.Groups[1].Value).ToList();
        foreach (var part in parts)
        {
            if (SimpleFromString(part, length, circular) && complement)
            {
                // Note the literal {text}: Biopython forgot the f prefix, so its warning quotes "{text}".
                throw new LocationParserError("double complement in '{text}'?");
            }
        }

        if (parts.Count == 0)
        {
            throw new PythonError("CompoundLocation without a part"); // join() and order(): a ValueError out of CompoundLocation
        }
    }

    // SimpleLocation.fromstring. Returns whether the location is on the reverse strand.
    private static bool SimpleFromString(string text, long? length, bool circular)
    {
        var reverse = false;
        if (text.StartsWith("complement(", StringComparison.Ordinal))
        {
            text = PythonText.Slice(text, 11, text.Length - 1);
            reverse = true;
        }

        // Try simple cases first for speed.
        var pieces = text.Split("..", StringSplitOptions.None);
        if (pieces.Length == 2 && TryPyInt(pieces[0], out var first) && TryPyInt(pieces[1], out var last))
        {
            var simpleStart = first - 1;
            if (simpleStart >= 0 && simpleStart < last)
            {
                return reverse;
            }
        }

        // Try the general case: "ref:text" (any other number of colons leaves the text alone).
        var colon = text.Split(':');
        if (colon.Length == 2)
        {
            text = colon[1];
        }

        var match = Category.Match(text);
        if (!match.Success)
        {
            throw new LocationParserError($"Could not parse feature location '{text}'");
        }

        var key = CategoryOrder.First(k => match.Groups[k].Success);
        if (match.Groups[key].Value != text)
        {
            throw new PythonError("assert value == text");
        }

        BigInteger startPosition;
        BigInteger endPosition;
        switch (key)
        {
            case "bond":
                text = PythonText.Slice(text, 5, text.Length - 1);
                startPosition = PositionFromString(text, -1);
                endPosition = PositionFromString(text, 0);
                break;
            case "solo":
                startPosition = PositionFromString(text, -1);
                endPosition = PositionFromString(text, 0);
                break;
            case "between":
                var ends = text.Split('^');
                var betweenStart = PyInt(ends[0]);
                var betweenEnd = PyInt(ends[1]);
                if (betweenStart + 1 != betweenEnd && !(length is { } n && betweenStart == n && betweenEnd == 1))
                {
                    throw new LocationParserError($"invalid feature location '{text}'");
                }

                startPosition = betweenStart;
                endPosition = betweenStart;
                break;
            default: // pair, within, oneof
                var halves = text.Split("..", StringSplitOptions.None);
                if (halves.Length != 2)
                {
                    throw new PythonError("too many values to unpack");
                }

                startPosition = PositionFromString(halves[0], -1);
                endPosition = PositionFromString(halves[1], 0);
                if (endPosition <= startPosition && length is > 0 && startPosition < length)
                {
                    // Looks like origin wrapping.
                    if (!circular)
                    {
                        throw new LocationParserError($"it appears that '{text}' is a feature that spans the origin, but the sequence topology is undefined");
                    }

                    if (endPosition < 0)
                    {
                        throw new PythonError("End location must be greater than or equal to start location"); // SimpleLocation(0, end)
                    }

                    return reverse;
                }

                break;
        }

        if (startPosition < 0)
        {
            throw new LocationParserError($"negative starting position in feature location '{text}'");
        }

        if (startPosition > endPosition)
        {
            throw new PythonError("End location must be greater than or equal to start location"); // SimpleLocation.__init__
        }

        return reverse;
    }

    // Position.fromstring: the integer value Python compares (the default of a fuzzy position, its int value).
    private static BigInteger PositionFromString(string text, int offset)
    {
        if (text == "?")
        {
            throw new PythonError("unknown position"); // not reachable: no category regex accepts "?" alone
        }

        if (text.StartsWith('?') || text.StartsWith('<') || text.StartsWith('>'))
        {
            return PyInt(text[1..]) + offset;
        }

        var within = WithinPositionRe.Match(text);
        if (within.Success)
        {
            return offset == -1 ? PyInt(within.Groups[1].Value) + offset : PyInt(within.Groups[2].Value) + offset;
        }

        var oneOf = OneOfPositionRe.Match(text);
        if (oneOf.Success)
        {
            var choices = oneOf.Groups[1].Value.Split(',').Select(p => PyInt(p) + offset).ToList();
            return offset == -1 ? choices.Min() : choices.Max();
        }

        return PyInt(text) + offset;
    }

    private static bool TryPyInt(string s, out BigInteger value)
    {
        try
        {
            value = PyInt(s);
            return true;
        }
        catch (PythonError)
        {
            value = default;
            return false;
        }
    }

    /// <summary>Python's <c>int(str)</c>: surrounding whitespace, an optional sign, decimal digits (any script) with single underscores between them.</summary>
    private static BigInteger PyInt(string s)
    {
        var t = PythonText.Trim(s);
        var negative = false;
        if (t.Length > 0 && t[0] is '+' or '-')
        {
            negative = t[0] == '-';
            t = t[1..];
        }

        if (t.Length == 0 || t[0] == '_' || t[^1] == '_' || t.Contains("__", StringComparison.Ordinal))
        {
            throw new PythonError($"invalid literal for int(): {s}");
        }

        BigInteger value = 0;
        foreach (var c in t.Where(c => c != '_'))
        {
            var digit = char.IsDigit(c) ? (int)char.GetNumericValue(c) : -1;
            if (digit < 0)
            {
                throw new PythonError($"invalid literal for int(): {s}");
            }

            value = (value * 10) + digit;
        }

        return negative ? -value : value;
    }
}
