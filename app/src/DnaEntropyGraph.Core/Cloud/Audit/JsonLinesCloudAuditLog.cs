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

    // How long a write waits for another process (or another instance) that is rotating. Short on purpose: on timeout the line is appended
    // without rotating, so the audit never holds a run up.
    private static readonly TimeSpan MutexWait = TimeSpan.FromMilliseconds(250);

    private Mutex? _mutex;

    private bool _mutexUnavailable;

    /// <summary>
    /// The machine-wide mutex that serialises rotate+append across processes (and across instances in one process) sharing this log file:
    /// keyed by a hash of the full path, so it is a valid kernel object name whatever the path holds and names nothing about the user.
    /// </summary>
    public string MutexName => "Global\\deg-cloud-audit-" + Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(System.IO.Path.GetFullPath(Path).ToUpperInvariant())))[..32];

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

                var held = TryEnter();
                try
                {
                    // Rotation is only ever done while holding the lock: two processes both seeing a full file would otherwise each move
                    // it, and the second move would put a nearly empty file over the first one's full generation.
                    if (held)
                    {
                        TryRotate();
                    }

                    using var stream = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    stream.Write(new UTF8Encoding(false).GetBytes(line));
                }
                finally
                {
                    if (held)
                    {
                        try
                        {
                            _mutex!.ReleaseMutex();
                        }
                        catch (ApplicationException)
                        {
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Lose the line, never the run (a full disk, a locked file, a path that is not usable).
        }
    }

    // True when this call owns the cross-process lock. False (a timeout, or a platform or session that will not give one) means append-only.
    private bool TryEnter()
    {
        if (_mutexUnavailable)
        {
            return false;
        }

        try
        {
            _mutex ??= new Mutex(false, MutexName);
            return _mutex.WaitOne(MutexWait);
        }
        catch (AbandonedMutexException)
        {
            // The holder died mid-write; the mutex is ours now and the file is still just lines.
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException or PlatformNotSupportedException or ArgumentException)
        {
            _mutexUnavailable = true;
            return false;
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
