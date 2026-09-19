using System.Buffers;
using System.Text;
using System.Text.RegularExpressions;

namespace DnaEntropyGraph.Core.Inputs;

/// <summary>
/// Raised when input cannot be turned into a valid A/C/G/T sequence. Mirrors
/// <c>worker/src/dna_entropy/validation/validators.py</c>'s <c>ValidationError</c>.
/// Every instance carries the manifest's single <c>INPUT_INVALID</c> error code
/// (docs/contract/error-codes.json) - the worker has exactly one code for every shape of
/// bad input at this layer, not one per rejection reason.
/// </summary>
public sealed class SequenceValidationException(string message) : Exception(message)
{
    public const string ErrorCode = "INPUT_INVALID";
}

/// <summary>A clean, model-ready sequence plus any non-fatal notices for the user.</summary>
public sealed record ValidatedSequence(string Seq, IReadOnlyList<string> Notices)
{
    public int Length => Seq.Length;
}

/// <summary>
/// Normalize and validate a raw pasted (or file-read) sequence into clean A/C/G/T. This is
/// the C# port of <c>worker/src/dna_entropy/validation/validators.py::validate_sequence</c>
/// (issue #64, Hard Rule 2: the app validates locally, with the SAME rules, before it
/// creates a single cloud resource). Every branch below - order, wording, and position
/// arithmetic - mirrors the Python function line-for-line; where they must diverge (this
/// port takes a strongly-typed <see cref="AmbiguityPolicy" /> so there is no "not one of
/// [...]" string-parsing branch to port) it is called out in a comment at that line.
/// </summary>
public static class SequenceValidator
{
    // The worker's own DEFAULT_MAX_LEN (config.py): the single-pass GPU window ceiling,
    // NOT the outer whole-input sanity bound (DEFAULT_MAX_TOTAL_LEN = 10_000_000). This is
    // deliberately the same default validate_sequence's OWN Python signature uses - real
    // call sites (a real run, or the `validate` CLI command) pass their own max_len drawn
    // from RunConfig, exactly as this port's callers are expected to.
    public const int DefaultMaxLen = 8192;

    // Below this length, entropy is dominated by the model's prior near the start; warn only.
    public const int DefaultMinLen = 10;

    private static readonly SearchValues<char> Acgt = SearchValues.Create("ACGT");
    private static readonly SearchValues<char> Ambiguity = SearchValues.Create("NRYSWKMBDHV");

    // issue #348 (app-side counterpart): readers/encoding.py's own fallback for a byte
    // that isn't valid UTF-8 turns it into exactly this one character (Unicode's own
    // "could not decode" signal) - never something a biologist typed themselves, so its
    // presence reliably signals an encoding problem, not a content problem.
    private const char ReplacementChar = '�';

    private static readonly Regex WhitespaceRegex = new(@"\s", RegexOptions.Compiled);
    private static readonly Regex DigitRegex = new(@"\d", RegexOptions.Compiled);

    /// <summary>
    /// Clean and validate <paramref name="raw" /> into a <see cref="ValidatedSequence" />.
    /// </summary>
    /// <param name="raw">Raw pasted/read text (may contain a header, whitespace, line numbers).</param>
    /// <param name="maxLen">Single-pass context cap; longer sequences are rejected.</param>
    /// <param name="rna">If true, convert U -&gt; T instead of rejecting RNA input.</param>
    /// <param name="minLen">Warn (do not fail) below this length.</param>
    /// <param name="ambiguityPolicy">
    /// What to do with an IUPAC ambiguity code - see <see cref="AmbiguityPolicy" /> and
    /// docs/science_and_formats.md. This function's own default is the strict one
    /// (<see cref="AmbiguityPolicy.Error" />), matching <c>validate_sequence</c>'s Python
    /// default; a real run's default (<c>RunOptions.AmbiguityPolicy</c> = Keep, matching
    /// <c>manifest.json</c>'s default) is a caller choice, not this function's.
    /// </param>
    /// <exception cref="SequenceValidationException">
    /// On RNA without <paramref name="rna" />=true, empty input, a non-ACGT
    /// non-ambiguity-code character (always an error, regardless of policy), an ambiguity
    /// code under <see cref="AmbiguityPolicy.Error" /> policy, or length over
    /// <paramref name="maxLen" />.
    /// </exception>
    public static ValidatedSequence Validate(
        string raw,
        int maxLen = DefaultMaxLen,
        bool rna = false,
        int minLen = DefaultMinLen,
        AmbiguityPolicy ambiguityPolicy = AmbiguityPolicy.Error)
    {
        ArgumentNullException.ThrowIfNull(raw);

        var notices = new List<string>();

        var (text, headerNotices) = StripLeadingHeader(raw);
        notices.AddRange(headerNotices);
        var (seq, normalizeNotices) = Normalize(text);
        notices.AddRange(normalizeNotices);

        // RNA handling must come before the strict A/C/G/T check, since U is not in ACGT.
        if (seq.Contains('U'))
        {
            if (rna)
            {
                var count = seq.Count(c => c == 'U');
                seq = seq.Replace('U', 'T');
                notices.Add($"Converted {count} U->T (RNA input).");
            }
            else
            {
                var pos = seq.IndexOf('U') + 1;
                throw new SequenceValidationException(
                    $"Found 'U' at position {pos}: this looks like RNA. "
                    + "Re-run with --rna to convert U->T, or paste a DNA sequence.");
            }
        }

        if (seq.Length == 0)
        {
            throw new SequenceValidationException("No nucleotides found after cleaning the input (empty sequence).");
        }

        var bad = new List<int>();
        for (var i = 0; i < seq.Length; i++)
        {
            if (!Acgt.Contains(seq[i]))
            {
                bad.Add(i);
            }
        }

        if (bad.Count > 0)
        {
            var nonIupac = bad.Where(i => !Ambiguity.Contains(seq[i])).ToList();
            if (nonIupac.Count > 0)
            {
                var nReplacement = bad.Count(i => seq[i] == ReplacementChar);
                if (nReplacement > 0)
                {
                    var i = bad.First(idx => seq[idx] == ReplacementChar);
                    throw new SequenceValidationException(
                        $"Found {nReplacement} character(s) that could not be decoded as "
                        + $"text (position {i + 1} is the first), which usually means the file "
                        + "was not saved as UTF-8 (e.g. Windows-1252 or another codepage). "
                        + "Re-save the file with UTF-8 encoding and try again.");
                }

                var i2 = nonIupac[0];
                var c = seq[i2];
                throw new SequenceValidationException(
                    $"Invalid character {ReprChar(c)} at position {i2 + 1} "
                    + $"({bad.Count} non-ACGT character(s) total). Only A, C, G, T are allowed.");
            }

            var codes = bad.Select(i => seq[i]).Distinct().OrderBy(c => c).ToList();
            var codesJoined = string.Join(", ", codes);
            switch (ambiguityPolicy)
            {
                case AmbiguityPolicy.Error:
                    {
                        var i = bad[0];
                        var c = seq[i];
                        throw new SequenceValidationException(
                            $"Ambiguity code {ReprChar(c)} at position {i + 1} "
                            + $"({bad.Count} total: {codesJoined}). This run's ambiguity policy is "
                            + "'error' (refuse). Choose 'keep' or 'mask' to run anyway, or clean the "
                            + "input to plain A/C/G/T.");
                    }
                case AmbiguityPolicy.Mask:
                    {
                        var sb = new StringBuilder(seq.Length);
                        foreach (var c in seq)
                        {
                            sb.Append(Acgt.Contains(c) ? c : 'N');
                        }
                        seq = sb.ToString();
                        notices.Add(
                            $"Masked {bad.Count} ambiguity code(s) ({codesJoined}) to 'N'; entropy "
                            + "at those positions reflects the model's prediction for 'N', not the "
                            + "original code (docs/science_and_formats.md).");
                        break;
                    }
                default: // AmbiguityPolicy.Keep
                    notices.Add(
                        $"Kept {bad.Count} ambiguity code(s) ({codesJoined}); entropy at those "
                        + "positions reflects the model's prediction for that exact code, not a "
                        + "definite base (docs/science_and_formats.md).");
                    break;
            }
        }

        if (seq.Length > maxLen)
        {
            throw new SequenceValidationException(
                $"Sequence length {seq.Length} exceeds the single-pass cap of {maxLen} nt. "
                + "Longer loci need windowing (future work); raise --max-len only if the GPU "
                + "has the VRAM.");
        }

        if (seq.Length < minLen)
        {
            notices.Add(
                $"Warning: sequence is short ({seq.Length} nt); entropy near the start is "
                + "dominated by the model's prior.");
        }

        return new ValidatedSequence(seq, notices);
    }

    /// <summary>Drop a single leading FASTA-style header line (<c>&gt;...</c>) if present.</summary>
    private static (string Text, List<string> Notices) StripLeadingHeader(string raw)
    {
        var notices = new List<string>();
        var lines = SplitLines(raw);
        for (var idx = 0; idx < lines.Count; idx++)
        {
            var line = lines[idx];
            if (line.Trim().Length == 0)
            {
                continue; // skip blank lines before the first content line
            }
            if (line.TrimStart().StartsWith('>'))
            {
                // Never the header text itself (issue #253) - just that one was dropped,
                // and how long it was.
                notices.Add($"Ignored a leading FASTA header line ({PrivacySafeText.DescribeLen(line.Trim())}).");
                lines.RemoveAt(idx);
            }
            break;
        }
        return (string.Join("\n", lines), notices);
    }

    /// <summary>Remove whitespace and digits (e.g. line numbers); uppercase. No U-&gt;T here.</summary>
    private static (string Seq, List<string> Notices) Normalize(string text)
    {
        var notices = new List<string>();
        var noWhitespace = WhitespaceRegex.Replace(text, string.Empty);
        var nDigits = noWhitespace.Count(char.IsDigit);
        if (nDigits > 0)
        {
            notices.Add($"Removed {nDigits} digit character(s) (e.g. line numbers).");
        }
        var seq = DigitRegex.Replace(noWhitespace, string.Empty).ToUpperInvariant();
        return (seq, notices);
    }

    /// <summary>Split like Python's <c>str.splitlines()</c> for the header-line sniff above.</summary>
    private static List<string> SplitLines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal)
            .Split('\n')
            .ToList();

    // A simplified stand-in for Python's repr() of a single character: our inputs here are
    // always an uppercased sequence character (a stray letter/punctuation a user pasted,
    // or an IUPAC code), never a full string with quotes/control characters to escape, so
    // the fixture this is checked against never exercises anything Python's real repr()
    // would escape differently.
    private static string ReprChar(char c) => $"'{c}'";
}
