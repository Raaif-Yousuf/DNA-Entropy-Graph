using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace DnaEntropyGraph.Core.Inputs;

/// <summary>
/// A light local read of a SnapGene <c>.dna</c> file (issue #685, Hard Rule 2): a C# port of the packet walk in the
/// worker's <c>readers/snapgene.py</c>. The file is a run of packets (a type byte, a big-endian uint32 length, the data).
/// Like the worker it refuses a damaged or hostile file by name before anything parses the metadata: a cut-off packet, an
/// XML packet over 8 MiB, a DOCTYPE or ENTITY declaration, no DNA packet. A byte at or above 0x80 in the DNA packet is
/// handed on as a non-ACGT character, so the shared validator refuses it with its position (the worker's own rule).
/// </summary>
public static partial class SnapGeneLite
{
    private const byte CookiePacket = 0x09;
    private const byte DnaPacket = 0x00;
    private const byte FeaturesPacket = 0x0A;

    /// <summary>One metadata packet is capped on its own, far below the whole-file cap (<c>_MAX_XML_PACKET_BYTES</c>).</summary>
    public const int MaxXmlPacketBytes = 8 * 1024 * 1024;

    // SnapGene never writes a DTD. Whitespace is allowed after "<!" because a lenient XML reader would.
    [GeneratedRegex(@"<!\s*(?:doctype|entity)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Dtd();

    [GeneratedRegex(@"<Feature\b[^>]*\btype=""gene""", RegexOptions.CultureInvariant)]
    private static partial Regex GeneFeature();

    public static GenBankLiteResult ReadFile(string path) => Read(File.ReadAllBytes(path));

    public static GenBankLiteResult Read(byte[] raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (!SequenceSniffer.IsSnapGene(raw))
        {
            throw Refuse("it does not start with SnapGene's file header");
        }

        string? dna = null;
        var genes = 0;
        var pos = 0;
        while (pos < raw.Length)
        {
            if (pos + 5 > raw.Length)
            {
                throw Refuse("a packet is cut off, so the file looks truncated");
            }
            var type = raw[pos];
            var length = BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(pos + 1, 4));
            var start = pos + 5;
            if (length > (uint)(raw.Length - start))
            {
                throw Refuse("a packet is cut off, so the file looks truncated");
            }
            var data = raw.AsSpan(start, (int)length);
            pos = start + (int)length;

            if (type == DnaPacket)
            {
                // data[0] is the topology flag byte; the sequence follows. Only the first DNA packet is the record.
                if (dna is null)
                {
                    dna = DnaText(data);
                }
            }
            else if (type != CookiePacket)
            {
                if (length > MaxXmlPacketBytes)
                {
                    throw Refuse("a metadata packet is larger than 8 MiB, which a plasmid map never is");
                }
                var xml = Encoding.UTF8.GetString(data);
                if (Dtd().IsMatch(xml))
                {
                    throw Refuse("a packet contains a DOCTYPE or ENTITY declaration, which SnapGene never writes");
                }
                if (type == FeaturesPacket)
                {
                    genes += GeneFeature().Count(xml);
                }
            }
        }

        if (dna is null)
        {
            throw Refuse("it has no DNA sequence packet");
        }
        if (dna.Length == 0 || dna.All(c => c == 'N'))
        {
            throw Refuse("its DNA packet has no nucleotide sequence");
        }

        var record = new GenBankLiteRecord(string.Empty, dna, genes, 0, 1);
        return new GenBankLiteResult([record], []);
    }

    private static string DnaText(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder(Math.Max(0, data.Length - 1));
        for (var i = 1; i < data.Length; i++)
        {
            var b = data[i];
            // A byte at or above 0x80 becomes its Latin-1 character (never U+FFFD, which the validator reports as a text-encoding
            // problem): no Latin-1 letter folds into a base, so the validator refuses it as an invalid character with its position.
            sb.Append(b is >= (byte)'a' and <= (byte)'z' ? (char)(b - 32) : (char)b);
        }
        return sb.ToString();
    }

    private static GenBankReadException Refuse(string reason)
        => new($"Could not read the SnapGene file ({reason}).");
}
