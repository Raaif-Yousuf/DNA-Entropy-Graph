using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;

namespace DnaEntropyGraph.Core.Diagnostics;

/// <summary>
/// Builds the support zip (issue #106) from the app data folder and the run history. Pure: no disk, no clock, no
/// environment; everything arrives as arguments, so a test seeds a fake folder and greps the result.
/// <para>
/// Nothing is copied unless a rule below names it (an allowlist of paths, not a blocklist): <c>auth/</c> (the DPAPI
/// token files), <c>inputs/</c> and <c>pasted/</c> (the user's sequences), the database and every output file are
/// never read. Everything copied is redacted by <see cref="DiagnosticsRedactor"/>, and the finished entries pass
/// <see cref="DiagnosticsLeakScan"/> before one byte of zip exists.
/// </para>
/// </summary>
public static partial class DiagnosticsBundleBuilder
{
    /// <summary>Settings keys whose values are safe to send. Every other key is listed by name with its value omitted.</summary>
    private static readonly HashSet<string> SafeSettingKeys = new(StringComparer.Ordinal) { "Theme", InstallationId.SettingsKey };

    // A log is cut to its last 2 MB of text: the end is what explains a failure.
    private const int MaxLogChars = 2_000_000;

    private static readonly JsonSerializerOptions Indented = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    [GeneratedRegex(@"^runs/[A-Za-z0-9_-]{1,64}/(?:status\.json|result\.json)$")]
    private static partial Regex RunJson();

    [GeneratedRegex(@"^runs/[A-Za-z0-9_-]{1,64}/progress(?:\.\d+)?\.jsonl$")]
    private static partial Regex RunJsonLines();

    [GeneratedRegex(@"^(?:logs/[A-Za-z0-9_.-]{1,100}|runs/[A-Za-z0-9_-]{1,64}/logs/[A-Za-z0-9_.-]{1,100})\.(?:log|txt)$")]
    private static partial Regex LogFile();

    /// <exception cref="DiagnosticsLeakException">Sequence-like text survived redaction; no zip is produced.</exception>
    public static byte[] Build(IDiagnosticsSource source, IReadOnlyList<RunRecord> runs, DiagnosticsInfo info)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(info);

        // The file names the history knows are redacted wherever they turn up in a log or a status file.
        var sensitive = info.SensitiveValues.Concat(runs.SelectMany(SensitiveFrom)).ToList();
        var redactor = new DiagnosticsRedactor(info.UserProfilePath, sensitive);

        var entries = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var settings = RedactSettings(source, redactor);

        entries["README.txt"] = Readme;
        entries["versions.json"] = JsonSerializer.Serialize(
            new
            {
                appVersion = info.AppVersion,
                os = info.OsDescription,
                dotNet = info.DotNetVersion,
                webView2 = info.WebView2Version ?? "not found",
                installationId = settings.InstallationId,
                createdUtc = info.CreatedUtc,
            },
            Indented);
        entries["settings.json"] = JsonSerializer.Serialize(settings.Values, Indented);
        entries["run-history.json"] = JsonSerializer.Serialize(runs.Select(ToHistoryRow), Indented);

        foreach (var path in source.ListFiles().OrderBy(p => p, StringComparer.Ordinal))
        {
            var copy = CopyAllowedFile(source, redactor, path);
            if (copy is not null)
            {
                entries["files/" + path] = copy;
            }
        }

        var leak = DiagnosticsLeakScan.FindLeak(entries);
        if (leak is not null)
        {
            throw new DiagnosticsLeakException(leak);
        }

        return Zip(entries, info.CreatedUtc);
    }

    private static string? CopyAllowedFile(IDiagnosticsSource source, DiagnosticsRedactor redactor, string path)
    {
        var isJson = RunJson().IsMatch(path);
        var isJsonLines = RunJsonLines().IsMatch(path);
        if (!isJson && !isJsonLines && !LogFile().IsMatch(path))
        {
            return null;
        }

        var bytes = source.TryRead(path);
        if (bytes is null)
        {
            return null;
        }

        var text = Encoding.UTF8.GetString(bytes);
        if (text.Length > MaxLogChars)
        {
            text = text[^MaxLogChars..];
        }

        if (isJson)
        {
            return redactor.RedactJson(text);
        }

        return isJsonLines ? redactor.RedactJsonLines(text) : redactor.RedactText(text).Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static (string? InstallationId, SortedDictionary<string, string> Values) RedactSettings(IDiagnosticsSource source, DiagnosticsRedactor redactor)
    {
        var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
        string? installationId = null;
        var bytes = source.TryRead("settings.json");
        if (bytes is null)
        {
            return (null, values);
        }

        Dictionary<string, string>? raw;
        try
        {
            raw = JsonSerializer.Deserialize<Dictionary<string, string>>(Encoding.UTF8.GetString(bytes));
        }
        catch (JsonException)
        {
            return (null, values);
        }

        foreach (var (key, value) in raw ?? [])
        {
            // Key names are fixed identifiers; a value is only ever copied for a key on the allowlist.
            var kept = SafeSettingKeys.Contains(key);
            values[key] = kept ? redactor.RedactText(value) : "<omitted>";
            if (key == InstallationId.SettingsKey)
            {
                installationId = values[key];
            }
        }

        return (installationId, values);
    }

    private static IEnumerable<string> SensitiveFrom(RunRecord run)
    {
        foreach (var text in new[] { run.Name, run.OutputDir is null ? null : Path.GetFileName(run.OutputDir.TrimEnd('\\', '/')) })
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                yield return text;
                yield return Path.GetFileNameWithoutExtension(text);
            }
        }
    }

    // The history as an allowlist of fields: identifiers, states, error codes, timings and hardware. Never the name,
    // the error detail (free text), the output folder, the project, bucket or VM names, notes or tags.
    private static object ToHistoryRow(RunRecord run) => new
    {
        jobId = run.JobId,
        phase = run.Phase.ToString(),
        target = run.Target,
        isBatch = run.IsBatch,
        errorCode = run.ErrorCode,
        createdUtc = run.CreatedUtc,
        startedAt = run.StartedAt,
        vmReadyAt = run.VmReadyAt,
        finishedAt = run.FinishedAt,
        machineType = run.MachineType,
        gpuType = run.GpuType,
        isSpot = run.IsSpot,
        vmReused = run.VmReused,
        vmSeconds = run.VmSeconds,
        estimatedCostUsd = run.EstimatedCostUsd,
        actualCostUsd = run.ActualCostUsd,
        cloudResultsDeleted = run.CloudResultsDeleted,
        appVersion = run.AppVersion,
        workerVersion = run.WorkerVersion,
        workerImageDigest = run.WorkerImageDigest,
        contractVersion = run.ContractVersion,
        installationId = run.InstallationId,
        imported = run.Imported,
    };

    private static byte[] Zip(SortedDictionary<string, string> entries, DateTimeOffset createdUtc)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in entries)
            {
                var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
                // A zip cannot hold a time before 1980.
                entry.LastWriteTime = createdUtc.Year < 1980 ? new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero) : createdUtc;
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false)) { NewLine = "\n" };
                writer.Write(text);
            }
        }

        return buffer.ToArray();
    }

    private const string Readme =
        "DNA Entropy Graph diagnostics\n" +
        "\n" +
        "Included: app and worker logs, run history (ids, states, error codes, times), the status, progress and\n" +
        "result files the app keeps for each run, settings with private values left out, version numbers and the\n" +
        "installation id.\n" +
        "\n" +
        "Never included: sequences, input file names, output files, your email, sign-in tokens. Paths inside your\n" +
        "Windows profile are shown as <user>.\n";
}
