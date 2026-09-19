namespace DnaEntropyGraph.Core.Inputs;

/// <summary>Which reader a given input routes to. Mirrors <c>readers/detect.py</c>'s three source kinds.</summary>
public enum InputKind
{
    GenBank,
    Fasta,
    Paste,
}

/// <summary>
/// Detect the input file kind so the app can route it to the right local reader before it
/// ever creates a cloud resource (Hard Rule 2). C# port of
/// <c>worker/src/dna_entropy/readers/detect.py::detect_kind</c> - order of evidence: file
/// extension first, then a content sniff of the first non-blank line. A null path (paste
/// box text, no file involved) is always <see cref="InputKind.Paste" />.
/// </summary>
public static class SequenceSniffer
{
    private static readonly HashSet<string> GenBankExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".gb", ".gbk", ".genbank", ".gbff",
    };

    private static readonly HashSet<string> FastaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".fa", ".fasta", ".fna", ".ffn",
    };

    /// <summary>
    /// Return the detected kind for <paramref name="path" /> (null =&gt; paste). Reads the
    /// file's content only when the extension is unrecognised; a file that cannot be read
    /// at all (missing, locked) is treated as paste, exactly like the worker's own
    /// <c>except OSError: return PASTE</c> fallback - callers reach a definite verdict
    /// either way, rather than surfacing an I/O exception at sniff time.
    /// </summary>
    public static InputKind DetectKind(string? path)
    {
        if (path is null)
        {
            return InputKind.Paste;
        }

        var ext = Path.GetExtension(path);
        if (GenBankExtensions.Contains(ext))
        {
            return InputKind.GenBank;
        }
        if (FastaExtensions.Contains(ext))
        {
            return InputKind.Fasta;
        }

        string head;
        try
        {
            head = TextDecoder.ReadText(path);
        }
        catch (IOException)
        {
            return InputKind.Paste;
        }
        catch (UnauthorizedAccessException)
        {
            return InputKind.Paste;
        }

        return DetectKindFromContent(head);
    }

    /// <summary>
    /// Sniff a kind directly from already-read text, for a caller that has the bytes in
    /// hand (a dropped file already staged, or a paste box's raw text) and does not want a
    /// second disk read. Same first-non-blank-line rule as <see cref="DetectKind" />.
    /// </summary>
    public static InputKind DetectKindFromContent(string text)
    {
        foreach (var rawLine in text.Split('\n'))
        {
            var s = rawLine.Trim('\r', ' ', '\t').Trim();
            if (s.Length == 0)
            {
                continue;
            }
            if (s.StartsWith("LOCUS", StringComparison.Ordinal))
            {
                return InputKind.GenBank;
            }
            if (s.StartsWith('>'))
            {
                return InputKind.Fasta;
            }
            break;
        }
        return InputKind.Paste;
    }
}
