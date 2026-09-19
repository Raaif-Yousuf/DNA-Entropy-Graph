using System.Security.Cryptography;
using System.Text;

namespace DnaEntropyGraph.Core.Inputs;

/// <summary>
/// Privacy-safe stand-ins for user content in a validation notice, mirroring
/// <c>worker/src/dna_entropy/redact.py</c> (issue #253). A notice can say *something*
/// useful about a piece of user text (how long it was, that two were identical) without
/// ever printing the text itself - a FASTA/GenBank header, a pasted sequence, a file name.
/// </summary>
public static class PrivacySafeText
{
    /// <summary>"12 chars" (or "1 char") - never the text itself, just how long it was.</summary>
    public static string DescribeLen(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var n = text.Length;
        return n == 1 ? "1 char" : $"{n} chars";
    }

    /// <summary>
    /// An 8-hex-char SHA-256 prefix of <paramref name="text" />: enough to tell "two of
    /// these were the same string" apart in a notice without the string itself ever being
    /// written anywhere. Not a security control - just a correlation aid.
    /// </summary>
    public static string Fingerprint(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var bytes = Encoding.UTF8.GetBytes(text);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash)[..8];
    }
}
