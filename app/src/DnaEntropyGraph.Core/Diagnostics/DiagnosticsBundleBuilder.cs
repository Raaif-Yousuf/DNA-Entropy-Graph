using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    // No file is read past this: a longer log is cut to its last 2 MB (the end is what explains a failure), and a longer
    // JSON file, which cannot be cut and stay valid, is left out with a note.
    private const int MaxFileBytes = 2_000_000;

    private const int MaxSettingsBytes = 256_000;

    private static readonly JsonSerializerOptions Indented = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private enum FileKind
    {
        Status,
        Result,
        Progress,
        Log,
    }

    private sealed record Loaded(string Path, FileKind Kind, string Text, bool Truncated);

    [GeneratedRegex(@"^runs/[A-Za-z0-9_-]{1,64}/(?:status\.json|result\.json)$")]
    private static partial Regex RunJson();

    [GeneratedRegex(@"^runs/[A-Za-z0-9_-]{1,64}/progress(?:\.\d+)?\.jsonl$")]
    private static partial Regex RunJsonLines();

    [GeneratedRegex(@"^(?:logs/[A-Za-z0-9_.-]{1,100}|runs/[A-Za-z0-9_-]{1,64}/logs/[A-Za-z0-9_.-]{1,100})\.(?:log|txt)$")]
    private static partial Regex LogFile();

    /// <exception cref="DiagnosticsLeakException">Sequence-like or credential-like text survived redaction; no zip is produced.</exception>
    public static byte[] Build(IDiagnosticsSource source, IReadOnlyList<RunRecord> runs, DiagnosticsInfo info)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(info);

        // Phase 1: read every allowed file and collect the names the bundle must not carry. The scrub list is the
        // caller's own strings, every identifying field of the run history, the input names in each run's manifest and
        // options, and every key and string inside the parts of status and result files that are not copied (contig and
        // record names), so a log line naming any of them is scrubbed too.
        var scrub = new HashSet<string>(info.SensitiveValues, StringComparer.OrdinalIgnoreCase);
        foreach (var run in runs)
        {
            AddRunNames(run, scrub);
        }

        var loaded = new List<Loaded>();
        foreach (var path in source.ListFiles().OrderBy(p => p, StringComparer.Ordinal))
        {
            var kind = Classify(path);
            var file = kind is null ? null : source.TryRead(path, MaxFileBytes);
            if (kind is not null && file is not null)
            {
                var text = Encoding.UTF8.GetString(file.Bytes);
                loaded.Add(new Loaded(path, kind.Value, text, file.Truncated));
                HarvestNames(kind.Value, text, file.Truncated, scrub);
            }
        }

        // Phase 2: redact and assemble.
        var redactor = new DiagnosticsRedactor(info.UserProfilePath, scrub);
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

        foreach (var file in loaded)
        {
            var copy = Copy(file, redactor);
            if (copy is not null)
            {
                entries["files/" + file.Path] = copy;
            }
        }

        var leak = DiagnosticsLeakScan.FindLeak(entries);
        if (leak is not null)
        {
            throw new DiagnosticsLeakException(leak);
        }

        return Zip(entries, info.CreatedUtc);
    }

    private static FileKind? Classify(string path)
    {
        if (RunJson().IsMatch(path))
        {
            return path.EndsWith("/status.json", StringComparison.Ordinal) ? FileKind.Status : FileKind.Result;
        }

        if (RunJsonLines().IsMatch(path))
        {
            return FileKind.Progress;
        }

        return LogFile().IsMatch(path) ? FileKind.Log : null;
    }

    private static KeySpec? SpecFor(FileKind kind) => kind switch
    {
        FileKind.Status => KeySpec.Status,
        FileKind.Result => KeySpec.Result,
        FileKind.Progress => KeySpec.Progress,
        _ => null,
    };

    private static void AddRunNames(RunRecord run, HashSet<string> scrub)
    {
        foreach (var text in new[] { run.Name, run.OutputDir, run.ProjectId, run.Bucket, run.VmName, run.JobPrefix, run.Notes })
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                scrub.Add(text.TrimEnd('\\', '/'));
            }
        }

        // The manifest and options name every input (name, path, stem); neither is copied.
        foreach (var json in new[] { run.ManifestJson, run.OptionsJson })
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                continue;
            }

            try
            {
                DiagnosticsRedactor.Harvest(JsonNode.Parse(json), null, scrub);
            }
            catch (JsonException)
            {
                // Not JSON: nothing to harvest.
            }
        }
    }

    private static void HarvestNames(FileKind kind, string text, bool truncated, HashSet<string> scrub)
    {
        var spec = SpecFor(kind);
        if (spec is null || truncated)
        {
            return;
        }

        foreach (var line in kind == FileKind.Progress ? text.Split('\n') : [text])
        {
            try
            {
                DiagnosticsRedactor.Harvest(JsonNode.Parse(line), spec, scrub);
            }
            catch (JsonException)
            {
                // A line that is not JSON is dropped by the redactor; nothing to harvest.
            }
        }
    }

    private static string? Copy(Loaded file, DiagnosticsRedactor redactor)
    {
        var spec = SpecFor(file.Kind);
        if (file.Kind is FileKind.Status or FileKind.Result)
        {
            return file.Truncated ? "<omitted: file too large>" : redactor.RedactJson(file.Text, spec!);
        }

        // A cut file starts mid-line: drop that partial line and say so.
        var text = file.Truncated ? file.Text[(file.Text.IndexOf('\n', StringComparison.Ordinal) + 1)..] : file.Text;
        var body = file.Kind == FileKind.Progress ? redactor.RedactJsonLines(text, spec!) : redactor.RedactText(text);
        return file.Truncated ? $"[truncated: only the last {MaxFileBytes} bytes were read]\n{body}" : body;
    }

    private static (string? InstallationId, SortedDictionary<string, string> Values) RedactSettings(IDiagnosticsSource source, DiagnosticsRedactor redactor)
    {
        var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
        string? installationId = null;
        var bytes = source.TryRead("settings.json", MaxSettingsBytes)?.Bytes;
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
        "Included: worker and run logs, run history (ids, states, error codes, times), the status, progress and\n" +
        "result files the app keeps for each run, settings with private values left out, version numbers and the\n" +
        "installation id. Logs written by the app itself are included once the app writes them.\n" +
        "\n" +
        "Never included: sequences, input file names, output files, your email, sign-in tokens. Paths inside your\n" +
        "Windows profile are shown as <user>.\n";
}
