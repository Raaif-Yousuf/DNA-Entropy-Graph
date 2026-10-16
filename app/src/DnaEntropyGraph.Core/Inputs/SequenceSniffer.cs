namespace DnaEntropyGraph.Core.Inputs;

/// <summary>Which reader a given input routes to. Mirrors <c>readers/detect.py</c>'s three source kinds.</summary>
public enum InputKind
{
    GenBank,
    Fasta,
    Paste,

    /// <summary>An EMBL flat file (issue #685), read like GenBank.</summary>
    Embl,

    /// <summary>A SnapGene <c>.dna</c> file (issue #685), binary, read like GenBank.</summary>
    SnapGene,
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

    private static readonly HashSet<string> EmblExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".embl", ".emb",
    };

    /// <summary>Every extension <see cref="DetectKindByExtension"/> decides on (GenBank first, then EMBL, then FASTA).</summary>
    public static IReadOnlyCollection<string> KnownExtensions => [.. GenBankExtensions, .. EmblExtensions, .. FastaExtensions];

    /// <summary>
    /// True when <paramref name="head"/> (the first bytes of a file) is SnapGene's cookie packet: type 0x09, a 4-byte length, then
    /// <c>SnapGene</c>. Port of <c>readers/snapgene.py::is_snapgene</c>; the magic bytes decide whatever the extension says.
    /// </summary>
    public static bool IsSnapGene(ReadOnlySpan<byte> head)
        => head.Length >= 13 && head[0] == 0x09 && head.Slice(5, 8).SequenceEqual("SnapGene"u8);

    /// <summary>Reads at most the first 13 bytes of <paramref name="path"/> and says whether they are SnapGene's cookie. False for a file that cannot be read.</summary>
    public static bool FileIsSnapGene(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> head = stackalloc byte[13];
            var read = stream.ReadAtLeast(head, 13, throwOnEndOfStream: false);
            return IsSnapGene(head[..read]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

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

        // detect.py: SnapGene is binary, so its magic bytes decide before any extension does.
        if (FileIsSnapGene(path))
        {
            return InputKind.SnapGene;
        }

        if (DetectKindByExtension(path) is { } byExtension)
        {
            return byExtension;
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

    /// <summary>The kind a known extension names, or null when the extension is unknown (content must decide). Reads nothing.</summary>
    public static InputKind? DetectKindByExtension(string path)
    {
        var ext = Path.GetExtension(path);
        if (GenBankExtensions.Contains(ext))
        {
            return InputKind.GenBank;
        }
        if (EmblExtensions.Contains(ext))
        {
            return InputKind.Embl;
        }
        return FastaExtensions.Contains(ext) ? InputKind.Fasta : null;
    }

    /// <summary>
    /// Sniff a kind directly from already-read text, for a caller that has the bytes in
    /// hand (a dropped file already staged, or a paste box's raw text) and does not want a
    /// second disk read. Same first-non-blank-line rule as <see cref="DetectKind" />.
    /// </summary>
    public static InputKind DetectKindFromContent(string text)
    {
        // Python str.splitlines() and str.strip(), as detect.py does (see PythonText).
        foreach (var rawLine in PythonText.SplitLines(text))
        {
            var s = PythonText.Trim(rawLine);
            if (s.Length == 0)
            {
                continue;
            }
            if (s.StartsWith("LOCUS", StringComparison.Ordinal))
            {
                return InputKind.GenBank;
            }
            if (s.StartsWith("ID   ", StringComparison.Ordinal))
            {
                return InputKind.Embl;
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
