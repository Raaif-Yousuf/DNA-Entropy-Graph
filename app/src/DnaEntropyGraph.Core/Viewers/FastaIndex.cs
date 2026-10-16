using System.Security.Cryptography;
using System.Text;

namespace DnaEntropyGraph.Core.Viewers;

/// <summary>One line of a samtools <c>.fai</c>: record name, bases, byte offset of the first base, bases per line, bytes per line (with the line ending).</summary>
public sealed record FastaIndexEntry(string Name, long Length, long Offset, int LineBases, int LineWidth);

/// <summary>
/// Issue #497: writes a samtools-format <c>.fai</c> so igv.js can read a long FASTA by byte range instead of loading the whole file
/// into the page. Streams the file through a small buffer (a 1 GB FASTA never sits in memory) and refuses what <c>samtools faidx</c>
/// refuses: bases before a header, uneven line lengths inside a record, mixed line endings, a duplicate record name.
/// </summary>
public static class FastaIndexWriter
{
    public static List<FastaIndexEntry> Build(Stream fasta, int bufferSize = 1 << 16)
    {
        var state = new Builder();
        var buffer = new byte[Math.Max(1, bufferSize)];
        int n;
        while ((n = fasta.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i < n; i++)
            {
                state.Feed(buffer[i]);
            }
        }

        return state.Finish();
    }

    public static void Write(IEnumerable<FastaIndexEntry> entries, TextWriter writer)
    {
        foreach (var e in entries)
        {
            writer.Write($"{e.Name}\t{e.Length}\t{e.Offset}\t{e.LineBases}\t{e.LineWidth}\n");
        }
    }

    /// <summary>Builds the index of <paramref name="fastaPath"/> into <paramref name="faiPath"/> (temp file, then move; nothing is left behind on failure).</summary>
    public static void BuildTo(string fastaPath, string faiPath)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(faiPath))!;
        Directory.CreateDirectory(folder);
        var temp = Path.Combine(folder, Path.GetFileName(faiPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            List<FastaIndexEntry> entries;
            using (var fasta = new FileStream(fastaPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.SequentialScan))
            {
                entries = Build(fasta);
            }

            using (var writer = new StreamWriter(temp, false, new UTF8Encoding(false)))
            {
                Write(entries, writer);
            }

            File.Move(temp, faiPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    // A byte-at-a-time line state machine; the first byte of a line decides whether it is a header or sequence.
    private sealed class Builder
    {
        private readonly List<FastaIndexEntry> _entries = [];
        private readonly HashSet<string> _names = new(StringComparer.Ordinal);
        private readonly List<byte> _header = [];

        private long _pos;
        private bool _atLineStart = true;
        private bool _inHeader;
        private int _bases;
        private int _term;

        private string? _name;
        private long _offset;
        private long _length;
        private int _lineBases;
        private int _lineWidth;
        private bool _shortSeen;
        private bool _blankSeen;

        public void Feed(byte b)
        {
            if (_atLineStart)
            {
                _atLineStart = false;
                _inHeader = b == (byte)'>';
                _bases = 0;
                _term = 0;
                _header.Clear();
                if (_inHeader)
                {
                    _pos++;
                    return;
                }
            }

            _pos++;
            if (b == (byte)'\n')
            {
                EndLine(atEof: false);
                _atLineStart = true;
            }
            else if (_inHeader)
            {
                _header.Add(b);
            }
            else if (b == (byte)'\r')
            {
                _term++;
            }
            else
            {
                _bases++;
            }
        }

        public List<FastaIndexEntry> Finish()
        {
            if (!_atLineStart)
            {
                EndLine(atEof: true);
            }

            CloseRecord();
            return _entries;
        }

        private void EndLine(bool atEof)
        {
            var terminator = atEof ? _term : _term + 1;
            if (_inHeader)
            {
                CloseRecord();
                var text = Encoding.UTF8.GetString(_header.ToArray()).TrimEnd('\r');
                var end = text.IndexOfAny([' ', '\t']);
                _name = end < 0 ? text : text[..end];
                if (_name.Length == 0 || !_names.Add(_name))
                {
                    throw new FormatException($"Record name '{_name}' is empty or appears twice.");
                }

                _offset = _pos;
                _length = 0;
                _lineBases = 0;
                _lineWidth = 0;
                _shortSeen = false;
                _blankSeen = false;
                return;
            }

            if (_name is null)
            {
                if (_bases == 0)
                {
                    return;
                }

                throw new FormatException("The file has bases before the first '>' header.");
            }

            if (_bases == 0)
            {
                _blankSeen = true;
                return;
            }

            if (_blankSeen || _shortSeen)
            {
                throw new FormatException($"Record '{_name}' has uneven line lengths.");
            }

            if (_lineBases == 0)
            {
                _lineBases = _bases;
                _lineWidth = _bases + terminator;
            }
            else if (_bases > _lineBases)
            {
                throw new FormatException($"Record '{_name}' has uneven line lengths.");
            }
            else if (_bases < _lineBases)
            {
                _shortSeen = true;
            }
            else if (!(atEof && _term == 0) && _bases + terminator != _lineWidth)
            {
                throw new FormatException($"Record '{_name}' mixes line endings.");
            }

            // A short last line keeps its own ending out of the check, but it must not outrun the line width.
            if (_bases < _lineBases && _bases + terminator > _lineWidth)
            {
                throw new FormatException($"Record '{_name}' mixes line endings.");
            }

            _length += _bases;
        }

        private void CloseRecord()
        {
            if (_name is not null)
            {
                _entries.Add(new FastaIndexEntry(_name, _length, _offset, _lineBases, _lineWidth));
                _name = null;
            }
        }
    }
}

/// <summary>Where a long FASTA's <c>.fai</c> lives. Always an app-owned folder (Hard Rule 14): never beside the user's file, never in a run's folder.</summary>
public interface IFastaIndexStore
{
    /// <summary>The folder every index is written to (and the viewer serves).</summary>
    string Directory { get; }

    /// <summary>The path of the index of <paramref name="fastaPath"/>, built first when it is missing or the FASTA changed. Throws <see cref="FormatException"/> or <see cref="IOException"/>.</summary>
    string EnsureIndex(string fastaPath);
}

public sealed class FastaIndexStore(string directory) : IFastaIndexStore
{
    public string Directory { get; } = directory;

    public string EnsureIndex(string fastaPath)
    {
        var info = new FileInfo(fastaPath);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{info.FullName.ToUpperInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}")))[..24].ToLowerInvariant();
        var fai = Path.Combine(Directory, key + ".fai");
        if (!File.Exists(fai))
        {
            FastaIndexWriter.BuildTo(fastaPath, fai);
        }

        return fai;
    }
}
