using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// Issue #261: the instance metadata the app attaches to every per-job VM, and
/// the guard that keeps it inside Compute Engine's limits.
///
/// <b>Why validation, not escaping.</b> <c>worker/vm/startup.sh</c> is a fixed
/// template (embedded here verbatim, one source of truth). Per-job values are
/// never written into the script text; the script reads them back from the
/// instance metadata server and then uses them inside a JSON document, a
/// <c>docker pull</c>/<c>docker run</c> command and a shell arithmetic
/// expansion (<c>shutdown -h "+$(( MAX_RUN_MIN + 15 ))"</c>, where a value such
/// as <c>a[$(cmd)]</c> would execute). There is no quoting layer in C# that
/// makes those safe, so every value is checked against the exact shape the
/// script can consume and anything else throws before a request is built.
/// </summary>
public static class StartupMetadata
{
    public const string ScriptKey = "startup-script";

    /// <summary>Compute Engine caps one metadata value at 256 KiB.</summary>
    public const int MaxValueBytes = 256 * 1024;

    /// <summary>Compute Engine caps the total of all metadata entries (keys and values) at 512 KiB.</summary>
    public const int MaxTotalBytes = 512 * 1024;

    private const int MaxRunMinutes = 100_000;

    private static readonly Regex JobIdPattern = new("^[a-z0-9_-]{1,63}$", RegexOptions.Compiled);

    private static readonly Regex BucketPattern = new("^[a-z0-9][a-z0-9._-]{1,61}[a-z0-9]$", RegexOptions.Compiled);

    // registry/path[:tag]@sha256:<64 hex>. The tag, if present, is cosmetic: docker resolves by digest.
    private static readonly Regex ImagePattern = new("^[a-z0-9][a-zA-Z0-9._/:-]{0,200}@sha256:[0-9a-f]{64}$", RegexOptions.Compiled);

    private static readonly Lazy<string> EmbeddedScript = new(LoadScript);

    /// <summary><c>worker/vm/startup.sh</c> with unix line endings (a CR would break <c>#!/usr/bin/env bash</c> on the VM).</summary>
    public static string Script => EmbeddedScript.Value;

    public static IReadOnlyDictionary<string, string> Build(
        string bucket,
        string jobId,
        string workerImage,
        bool expectGpu,
        AfterTaskAction lifecycle,
        TimeSpan maxRun)
    {
        Require(bucket is not null && BucketPattern.IsMatch(bucket), "bucket", "3-63 lowercase letters, digits, dots, underscores or hyphens");
        Require(jobId is not null && JobIdPattern.IsMatch(jobId), "job id", "1-63 lowercase letters, digits, underscores or hyphens");
        Require(workerImage is not null && ImagePattern.IsMatch(workerImage), "worker image", "a full reference pinned by digest (name@sha256:<64 hex digits>)");

        var minutes = (long)Math.Ceiling(maxRun.TotalMinutes);
        Require(minutes is >= 1 and <= MaxRunMinutes, "max run time", $"between 1 and {MaxRunMinutes} minutes");

        var metadata = new Dictionary<string, string>
        {
            [ScriptKey] = Script,
            ["deg-bucket"] = bucket!,
            ["deg-job-id"] = jobId!,
            ["deg-worker-image"] = workerImage!,
            ["deg-expect-gpu"] = expectGpu ? "true" : "false",
            ["deg-lifecycle"] = lifecycle switch
            {
                AfterTaskAction.Delete => "delete",
                AfterTaskAction.KeepAlive => "keep",
                _ => "stop",
            },
            ["deg-max-run-min"] = minutes.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        EnsureSize(metadata);
        return metadata;
    }

    /// <summary>Throws naming the offending key when a value, or the total, exceeds Compute Engine's metadata limits (measured in UTF-8 bytes, as the API counts them).</summary>
    public static void EnsureSize(IReadOnlyDictionary<string, string> metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        long total = 0;
        foreach (var (key, value) in metadata)
        {
            var valueBytes = Encoding.UTF8.GetByteCount(value);
            if (valueBytes > MaxValueBytes)
            {
                throw new InvalidOperationException(
                    $"Metadata value '{key}' is {valueBytes} bytes; Compute Engine allows at most {MaxValueBytes} bytes per value.");
            }

            total += Encoding.UTF8.GetByteCount(key) + valueBytes;
        }

        if (total > MaxTotalBytes)
        {
            throw new InvalidOperationException(
                $"Metadata total is {total} bytes; Compute Engine allows at most {MaxTotalBytes} bytes in total.");
        }
    }

    private static void Require(bool condition, string what, string expected)
    {
        if (!condition)
        {
            throw new ArgumentException($"The {what} given for the VM startup metadata is not valid: it must be {expected}.");
        }
    }

    private static string LoadScript()
    {
        using var stream = typeof(StartupMetadata).Assembly.GetManifestResourceStream("startup.sh")
            ?? throw new InvalidOperationException("startup.sh is not embedded in DnaEntropyGraph.Core; check the EmbeddedResource item in its csproj.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Replace("\r\n", "\n");
    }
}
