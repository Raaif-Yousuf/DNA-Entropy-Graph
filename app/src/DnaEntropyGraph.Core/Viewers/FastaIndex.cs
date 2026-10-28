using System.Collections.Concurrent;
using System.Globalization;
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
    public static List<FastaIndexEntry> Build(Stream fasta, int bufferSize = 1 << 16, CancellationToken cancellationToken = default)
    {
        var state = new Builder();
        var buffer = new byte[Math.Max(1, bufferSize)];
        int n;
        while ((n = fasta.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
    public static void BuildTo(string fastaPath, string faiPath, CancellationToken cancellationToken = default)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(faiPath))!;
        Directory.CreateDirectory(folder);
        var temp = Path.Combine(folder, Path.GetFileName(faiPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            List<FastaIndexEntry> entries;
            using (var fasta = new FileStream(fastaPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.SequentialScan))
            {
                entries = Build(fasta, cancellationToken: cancellationToken);
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

    /// <summary>
    /// True when the stored index at <paramref name="faiPath"/> fits a FASTA of <paramref name="fastaLength"/> bytes: non-empty, every line five
    /// numeric columns, names unique, records in order, and the last record ends exactly where the file ends (only line breaks may follow).
    /// A truncated, damaged or stale index fails this and is rebuilt.
    /// </summary>
    public static bool IsValidFor(string faiPath, long fastaLength, string? fastaPath = null)
    {
        try
        {
            var lines = File.ReadAllLines(faiPath).Where(l => l.Length > 0).ToList();
            if (lines.Count == 0)
            {
                return false;
            }

            var names = new HashSet<string>(StringComparer.Ordinal);
            long previousEnd = 0;
            foreach (var line in lines)
            {
                var f = line.Split('\t');
                if (f.Length != 5
                    || f[0].Length == 0
                    || !names.Add(f[0])
                    || !long.TryParse(f[1], NumberStyles.None, CultureInfo.InvariantCulture, out var length)
                    || !long.TryParse(f[2], NumberStyles.None, CultureInfo.InvariantCulture, out var offset)
                    || !int.TryParse(f[3], NumberStyles.None, CultureInfo.InvariantCulture, out var lineBases)
                    || !int.TryParse(f[4], NumberStyles.None, CultureInfo.InvariantCulture, out var lineWidth)
                    || (lineBases > 0 && lineWidth < lineBases)
                    || offset < previousEnd)
                {
                    return false;
                }

                previousEnd = lineBases > 0 ? offset + (length / lineBases * lineWidth) + (length % lineBases) : offset;
                if (previousEnd > fastaLength)
                {
                    return false;
                }
            }

            if (fastaPath is null)
            {
                return fastaLength - previousEnd <= 1024;
            }

            using var fasta = new FileStream(fastaPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fasta.Seek(previousEnd, SeekOrigin.Begin);
            int b;
            while ((b = fasta.ReadByte()) >= 0)
            {
                if (b != '\n' && b != '\r')
                {
                    return false;
                }
            }

            return true;
        }
        catch (IOException)
        {
            return false;
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

    /// <summary>
    /// The path of the index of <paramref name="fastaPath"/>, built first when it is missing, damaged or the FASTA changed. Concurrent calls
    /// for one file build it once. Throws <see cref="FormatException"/>, <see cref="IOException"/> or <see cref="OperationCanceledException"/>.
    /// </summary>
    string EnsureIndex(string fastaPath, CancellationToken cancellationToken = default);
}

public sealed class FastaIndexStore(string directory, Action<string>? buildObserver = null) : IFastaIndexStore
{
    /// <summary>An index not used for this long is evicted by the sweep that runs once per process.</summary>
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromDays(30);

    private static readonly TimeSpan OrphanTempAge = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);
    private int _swept;

    public string Directory { get; } = directory;

    public string EnsureIndex(string fastaPath, CancellationToken cancellationToken = default)
    {
        System.IO.Directory.CreateDirectory(Directory);
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref _swept, 1) == 0)
        {
            Sweep(DefaultMaxAge);
        }

        var info = new FileInfo(fastaPath);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{info.FullName.ToUpperInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}")))[..24].ToLowerInvariant();
        var fai = Path.Combine(Directory, key + ".fai");
        var gate = _gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        gate.Wait(cancellationToken);
        try
        {
            if (File.Exists(fai) && FastaIndexWriter.IsValidFor(fai, info.Length, info.FullName))
            {
                Touch(fai);
                return fai;
            }

            buildObserver?.Invoke(fai);
            FastaIndexWriter.BuildTo(fastaPath, fai, cancellationToken);
            File.WriteAllText(Path.Combine(Directory, key + ".src"), info.FullName, new UTF8Encoding(false));
            return fai;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Housekeeping: deletes orphan temp files left by a killed build, indexes not used for <paramref name="maxAge"/>, and indexes whose FASTA
    /// no longer exists (each index has a <c>.src</c> note of the file it was made for). Never throws.
    /// </summary>
    public void Sweep(TimeSpan maxAge)
    {
        try
        {
            if (!System.IO.Directory.Exists(Directory))
            {
                return;
            }

            var now = DateTime.UtcNow;
            foreach (var tmp in System.IO.Directory.EnumerateFiles(Directory, "*.tmp"))
            {
                if (now - File.GetLastWriteTimeUtc(tmp) > OrphanTempAge)
                {
                    TryDelete(tmp);
                }
            }

            foreach (var fai in System.IO.Directory.EnumerateFiles(Directory, "*.fai").ToList())
            {
                var src = Path.ChangeExtension(fai, ".src");
                var expired = now - File.GetLastWriteTimeUtc(fai) > maxAge;
                var sourceGone = File.Exists(src) && !File.Exists(File.ReadAllText(src));
                if (expired || sourceGone)
                {
                    TryDelete(fai);
                    TryDelete(src);
                }
            }

            foreach (var src in System.IO.Directory.EnumerateFiles(Directory, "*.src").ToList())
            {
                if (!File.Exists(Path.ChangeExtension(src, ".fai")))
                {
                    TryDelete(src);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Housekeeping only: a locked file is swept next time.
        }
    }

    private static void Touch(string path)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
