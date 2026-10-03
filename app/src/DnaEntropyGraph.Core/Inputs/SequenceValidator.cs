using System.Buffers;
using System.Text;
using System.Globalization;

namespace DnaEntropyGraph.Core.Inputs;

/// <summary>
/// Raised when input cannot be turned into a valid A/C/G/T sequence. Mirrors
/// <c>worker/src/dna_entropy/validation/validators.py</c>'s <c>ValidationError</c>.
/// Every instance carries the manifest's single <c>INPUT_INVALID</c> error code
/// (docs/contract/error-codes.json) - the worker has exactly one code for every shape of
/// bad input at this layer, not one per rejection reason.
/// </summary>
public sealed class SequenceValidationException(
    string message,
    SequenceFailure reason = SequenceFailure.Other,
    int? position = null) : Exception(message)
{
    public const string ErrorCode = "INPUT_INVALID";

    /// <summary>Machine-readable cause (issue #479); the message wording is unchanged by it.</summary>
    public SequenceFailure Reason { get; } = reason;

    /// <summary>1-based position in the cleaned sequence (whitespace and digits removed), counted in code points like the worker; null when the failure has no single position.</summary>
    public int? Position { get; } = position;
}

/// <summary>Why a <see cref="SequenceValidationException" /> was raised, for callers that must not parse the message.</summary>
public enum SequenceFailure
{
    Other,
    Rna,
    Empty,
    Undecodable,
    InvalidCharacter,
    AmbiguityRefused,
    TooLong,
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
                var pos = CodePointPosition(seq, seq.IndexOf('U'));
                throw new SequenceValidationException(
                    $"Found 'U' at position {pos}: this looks like RNA. "
                    + "Re-run with --rna to convert U->T, or paste a DNA sequence.",
                    SequenceFailure.Rna,
                    pos);
            }
        }

        if (seq.Length == 0)
        {
            throw new SequenceValidationException(
                "No nucleotides found after cleaning the input (empty sequence).", SequenceFailure.Empty);
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
                // Counted in code points like the worker: a surrogate pair is one bad character.
                var badCount = bad.Count(i => !IsSecondOfPair(seq, i));
                var nReplacement = bad.Count(i => seq[i] == ReplacementChar);
                if (nReplacement > 0)
                {
                    var i = bad.First(idx => seq[idx] == ReplacementChar);
                    var replacementPos = CodePointPosition(seq, i);
                    throw new SequenceValidationException(
                        $"Found {nReplacement} character(s) that could not be decoded as "
                        + $"text (position {replacementPos} is the first), which usually means the file "
                        + "was not saved as UTF-8 (e.g. Windows-1252 or another codepage). "
                        + "Re-save the file with UTF-8 encoding and try again.",
                        SequenceFailure.Undecodable,
                        replacementPos);
                }

                var i2 = nonIupac[0];
                var invalidPos = CodePointPosition(seq, i2);
                throw new SequenceValidationException(
                    $"Invalid character {ShowChar(seq, i2)} at position {invalidPos} "
                    + $"({badCount} non-ACGT character(s) total). Only A, C, G, T are allowed.",
                    SequenceFailure.InvalidCharacter,
                    invalidPos);
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
                            + "input to plain A/C/G/T.",
                            SequenceFailure.AmbiguityRefused,
                            i + 1);
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
                + "has the VRAM.",
                SequenceFailure.TooLong);
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
        var lines = PythonText.SplitLines(raw);
        for (var idx = 0; idx < lines.Count; idx++)
        {
            var line = lines[idx];
            if (PythonText.Trim(line).Length == 0)
            {
                continue; // skip blank lines before the first content line
            }
            if (PythonText.TrimStart(line).StartsWith('>'))
            {
                // Never the header text itself (issue #253) - just that one was dropped,
                // and how long it was.
                notices.Add($"Ignored a leading FASTA header line ({PrivacySafeText.DescribeLen(PythonText.Trim(line))}).");
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
        // One pass over code points, mirroring Python: remove str whitespace, count isdigit()
        // characters, remove Unicode Nd (re \d, astral digits included), and uppercase ASCII
        // only (worker issue #468: ToUpperInvariant folds U+017F long s to ASCII 'S').
        // A non-ASCII character is left exactly as typed so the validator refuses it at its
        // true position. A lone surrogate is kept as one unit (EnumerateRunes would replace it).
        var sb = new StringBuilder(text.Length);
        var nDigits = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (PythonText.IsSpace(c))
            {
                continue;
            }
            var width = char.IsSurrogatePair(text, i) ? 2 : 1;
            if (CharUnicodeInfo.GetDigitValue(text, i) >= 0)
            {
                nDigits++;
            }
            if (CharUnicodeInfo.GetUnicodeCategory(text, i) == UnicodeCategory.DecimalDigitNumber)
            {
                i += width - 1;
                continue;
            }
            sb.Append(c is >= 'a' and <= 'z' ? (char)(c - 32) : c);
            if (width == 2)
            {
                sb.Append(text[++i]);
            }
        }
        if (nDigits > 0)
        {
            notices.Add($"Removed {nDigits} digit character(s) (e.g. line numbers).");
        }
        return (sb.ToString(), notices);
    }

    private static bool IsSecondOfPair(string s, int k) => char.IsLowSurrogate(s[k]) && k > 0 && char.IsHighSurrogate(s[k - 1]);

    /// <summary>1-based position counted in code points (the worker's Python str indexing), not UTF-16 units.</summary>
    private static int CodePointPosition(string seq, int utf16Index)
    {
        var n = 1;
        for (var k = 0; k < utf16Index; k++)
        {
            if (!IsSecondOfPair(seq, k))
            {
                n++;
            }
        }
        return n;
    }

    // Python: `repr(c) if c.isascii() else f"U+{ord(c):04X}"` (the message reaches the console,
    // Hard Rule 5, and a lookalike glyph would not tell the user which character to remove).
    private static string ShowChar(string seq, int index)
    {
        var c = seq[index];
        if (!char.IsAscii(c))
        {
            // A lone surrogate is named by its own unit; only a real pair is one code point.
            var cp = index + 1 < seq.Length && char.IsSurrogatePair(c, seq[index + 1]) ? char.ConvertToUtf32(c, seq[index + 1]) : c;
            return $"U+{cp:X4}";
        }
        return ReprChar(c);
    }
    // A simplified stand-in for Python's repr() of a single character: our inputs here are
    // always an uppercased sequence character (a stray letter/punctuation a user pasted,
    // or an IUPAC code), never a full string with quotes/control characters to escape, so
    // the fixture this is checked against never exercises anything Python's real repr()
    // would escape differently.
    private static string ReprChar(char c) => c switch
    {
        '\'' => "\"'\"",
        '\\' => "'\\\\'",
        '\n' => "'\\n'",
        '\r' => "'\\r'",
        '\t' => "'\\t'",
        < ' ' or '\u007f' => $"'\\x{(int)c:x2}'",
        _ => $"'{c}'",
    };
}
