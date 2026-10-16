using System.Text;
using System.Text.Json;

namespace DnaEntropyGraph.Core.Cloud.Audit;

/// <summary>
/// The append-only cloud audit log (issue #245): one JSON object per line, UTF-8, LF. When the file reaches <c>maxBytes</c> it is moved to
/// <c>name.1.ext</c> (replacing the previous one), so the log never holds more than about twice that. <see cref="Record"/> never throws: a
/// full disk, a locked file or a bad path loses the line and nothing else.
/// </summary>
public sealed class JsonLinesCloudAuditLog(string path, long maxBytes = 1_000_000) : ICloudAuditLog
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    private readonly object _gate = new();

    public string Path { get; } = path;

    /// <summary>Where the previous generation lives after a rotation.</summary>
    public string RotatedPath => System.IO.Path.Combine(
        System.IO.Path.GetDirectoryName(Path) ?? string.Empty,
        System.IO.Path.GetFileNameWithoutExtension(Path) + ".1" + System.IO.Path.GetExtension(Path));

    public void Record(CloudAuditEntry entry)
    {
        try
        {
            var line = JsonSerializer.Serialize(entry, Options) + "\n";
            lock (_gate)
            {
                var directory = System.IO.Path.GetDirectoryName(Path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                TryRotate();

                using var stream = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                stream.Write(new UTF8Encoding(false).GetBytes(line));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Lose the line, never the run (a full disk, a locked file, a path that is not usable).
        }
    }

    // A rotation that fails (the previous generation is open in a reader, an editor or antivirus) must not lose the line: the cap is
    // soft, the line goes into the current file, and the next write tries the rotation again.
    private void TryRotate()
    {
        try
        {
            if (File.Exists(Path) && new FileInfo(Path).Length >= maxBytes)
            {
                File.Move(Path, RotatedPath, overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
