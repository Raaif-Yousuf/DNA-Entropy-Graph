using System.Text;

namespace DnaEntropyGraph.Core.Inputs;

/// <summary>
/// The single point of encoding/BOM detection for every local text-reading input path in
/// the app, mirroring <c>worker/src/dna_entropy/readers/encoding.py</c> byte-for-byte
/// (issue #64/#330's app-side counterpart). Every reader under
/// <see cref="DnaEntropyGraph.Core.Inputs" /> decodes through <see cref="DecodeBytes" /> /
/// <see cref="ReadText" /> below, never <c>File.ReadAllText</c> directly, so a UTF-8 BOM or
/// a UTF-16 "Notepad Unicode" save is handled identically to the worker rather than
/// drifting across N call sites.
/// </summary>
public static class TextDecoder
{
    private static readonly byte[] Utf16LeBom = [0xFF, 0xFE];
    private static readonly byte[] Utf16BeBom = [0xFE, 0xFF];
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    /// <summary>
    /// Decode raw file/stream bytes into text, honouring a byte-order mark if present.
    ///
    /// - A UTF-16 BOM (either byte order) means the file genuinely IS UTF-16: decoded in
    ///   full via the matching explicit endianness, BOM bytes stripped, invalid sequences
    ///   replaced with U+FFFD (never thrown) - exactly what "Save As... Encoding: Unicode"
    ///   produces in Notepad on Windows.
    /// - A UTF-8 BOM is stripped before anything else ever sees the text, so the first
    ///   real character is the line's actual first character.
    /// - Anything else decodes as UTF-8 with replacement fallback (an undecodable byte
    ///   becomes a single U+FFFD rather than throwing).
    /// </summary>
    public static string DecodeBytes(byte[] raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        if (StartsWith(raw, Utf16LeBom))
        {
            return DecodeReplacing(Encoding.Unicode, raw, Utf16LeBom.Length);
        }
        if (StartsWith(raw, Utf16BeBom))
        {
            return DecodeReplacing(Encoding.BigEndianUnicode, raw, Utf16BeBom.Length);
        }
        if (StartsWith(raw, Utf8Bom))
        {
            return DecodeReplacing(Encoding.UTF8, raw, Utf8Bom.Length);
        }
        return DecodeReplacing(Encoding.UTF8, raw, 0);
    }

    /// <summary>Read <paramref name="path" /> from disk and decode it via <see cref="DecodeBytes" />.</summary>
    public static string ReadText(string path) => DecodeBytes(File.ReadAllBytes(path));

    private static string DecodeReplacing(Encoding baseEncoding, byte[] raw, int skip)
    {
        // A fresh Encoding instance with an explicit replacement fallback: .NET's shared
        // Encoding.UTF8/.Unicode/.BigEndianUnicode singletons already replace invalid
        // sequences with U+FFFD by default (never throw), matching Python's
        // errors="replace" - this just makes that behaviour explicit and independent of
        // any process-wide encoding configuration.
        var encoding = (Encoding)baseEncoding.Clone();
        encoding.DecoderFallback = new DecoderReplacementFallback("�");
        return encoding.GetString(raw, skip, raw.Length - skip);
    }

    private static bool StartsWith(byte[] raw, byte[] prefix)
    {
        if (raw.Length < prefix.Length)
        {
            return false;
        }
        for (var i = 0; i < prefix.Length; i++)
        {
            if (raw[i] != prefix[i])
            {
                return false;
            }
        }
        return true;
    }
}
