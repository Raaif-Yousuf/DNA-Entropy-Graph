using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Runs;

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
    public static byte[] Build(IDiagnosticsSource source, IReadOnlyList<RunRecord> runs, DiagnosticsInfo info, bool includeNotesAndTags = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(info);

        // Phase 1: read every allowed file and collect the names the bundle must not carry. The scrub list is the
        // caller's own strings, every identifying field of the run history, the input names in each run's manifest and
        // options, and every key and string inside the parts of status and result files that are not copied (contig and
        // record names), so a log line naming any of them is scrubbed too.
        var scrub = new HashSet<string>(info.SensitiveValues, StringComparer.OrdinalIgnoreCase);
        var userText = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ids = new JobIdWithholder(scrub);
        foreach (var run in runs)
        {
            ids.Map(run.JobId);
            AddRunNames(run, scrub);
            if (!includeNotesAndTags)
            {
                AddUserText(run, userText);
            }
        }

        var loaded = new List<Loaded>();
        foreach (var path in source.ListFiles().OrderBy(p => p, StringComparer.Ordinal))
        {
            var kind = Classify(path);
            if (kind is not null)
            {
                ids.MapPath(path);
            }

            var file = kind is null ? null : source.TryRead(path, MaxFileBytes);
            if (kind is not null && file is not null)
            {
                var text = Encoding.UTF8.GetString(file.Bytes);
                loaded.Add(new Loaded(path, kind.Value, text, file.Truncated));
                HarvestNames(kind.Value, text, file.Truncated, scrub);
            }
        }

        // Phase 2: redact and assemble.
        var redactor = new DiagnosticsRedactor(info.UserProfilePath, scrub, userText);
        var entries = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var settings = RedactSettings(source, redactor);

        entries["README.txt"] = info.ReadmeText.Replace("\r\n", "\n", StringComparison.Ordinal);
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
        entries["run-history.json"] = JsonSerializer.Serialize(runs.Select(run => includeNotesAndTags ? WithNotesAndTags(ToHistoryRow(run, ids.Map(run.JobId)), run, redactor, scrub) : ToHistoryRow(run, ids.Map(run.JobId))), Indented);

        // A copied log is stored under a neutral name: the original can carry a user, patient or input name. The mapping from
        // the original is written nowhere in the zip.
        var logCounters = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var file in loaded)
        {
            entries[EntryName(file, ids, logCounters)] = Copy(file, redactor, ids) ?? OmittedUnreadable;
        }

        // The scan backstops the redactor on the user's own identifiers (the same list the redactor used, plus the profile
        // path, so the Windows user name is covered even when the caller left it out of SensitiveValues).
        var leak = DiagnosticsLeakScan.Find(entries, [.. scrub, info.UserProfilePath]);
        if (leak is not null)
        {
            throw new DiagnosticsLeakException(leak);
        }

        return Zip(entries, info.CreatedUtc);
    }

    // Fixed words only: the leak scan blanks exactly this shape (and "<omitted: file too large>") before it checks identifiers.
    private const string OmittedUnreadable = "<omitted: file is not valid json>";

    private static string EntryName(Loaded file, JobIdWithholder ids, Dictionary<string, int> logCounters)
    {
        var path = ids.MapPath(file.Path);
        if (file.Kind != FileKind.Log)
        {
            return "files/" + path;
        }

        var directory = path[..path.LastIndexOf('/')];
        var number = logCounters.GetValueOrDefault(directory) + 1;
        logCounters[directory] = number;
        return $"files/{directory}/log-{number:00}.log";
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

    // Issue #141: a note and a tag are the user's own words. Unless the user ticked the box to share them they are kept out of the
    // history row and redacted from the copied logs as whole tokens. They are NOT added to the identifier scrub list: that list is
    // matched as substrings and re-checked by the leak scan, and a tag equal to a word the bundle itself writes ("completed",
    // "error") made every bundle fail or mangled ordinary log words (cold review of #141, issue #782).
    private static void AddUserText(RunRecord run, HashSet<string> userText)
    {
        foreach (var tag in RunTags.Parse(run.TagsJson))
        {
            userText.Add(tag);
        }

        if (!string.IsNullOrWhiteSpace(run.Notes))
        {
            userText.Add(run.Notes.Trim());
        }
    }

    private static void AddRunNames(RunRecord run, HashSet<string> scrub)
    {
        foreach (var text in new[] { run.Name, run.OutputDir, run.ProjectId, run.Bucket, run.VmName, run.JobPrefix })
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var value = text.TrimEnd('\\', '/');
            if (!JobId.MatchesConvention(run.JobId))
            {
                // Not an id the app made: it may hold user text, so it is never exempt and never shipped (see JobIdWithholder).
                scrub.Add(value);
            }
            else if (!IsOnlyTheJobId(value, run.JobId))
            {
                // A path ending in the id: the id is shipped on purpose, so only the part before it is scrubbed.
                scrub.Add(WithoutTrailingJobId(value, run.JobId));
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
            catch (Exception ex) when (DiagnosticsRedactor.IsUnreadableJson(ex))
            {
                // Not JSON: nothing to harvest.
            }
        }
    }

    // A run's name (or its bucket prefix, "jobs/<id>/") that is nothing but its own job id adds no information: the history
    // copies a standard job id verbatim on purpose, so scrubbing it would make the final scan refuse the bundle for the very
    // field it allows (MEASURED 2026-10-03, issue #626). Only an id in the app's own shape is shipped; any other id is
    // withheld by JobIdWithholder. Exactly "<id>" or "jobs/<id>" (the bucket prefix shape) is exempt: a longer path ending
    // in the id (an output folder under the user's profile) is not, and its part before the id is scrubbed.
    private static bool IsOnlyTheJobId(string value, string jobId)
    {
        value = value.Trim().Trim('\\', '/').Replace('\\', '/');
        return string.Equals(value, jobId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "jobs/" + jobId, StringComparison.OrdinalIgnoreCase);
    }

    private static string WithoutTrailingJobId(string value, string jobId)
    {
        var normalized = value.Replace('\\', '/');
        return normalized.EndsWith("/" + jobId, StringComparison.OrdinalIgnoreCase) ? value[..^(jobId.Length + 1)] : value;
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
            catch (Exception ex) when (DiagnosticsRedactor.IsUnreadableJson(ex))
            {
                // A line that is not JSON is dropped by the redactor; nothing to harvest.
            }
        }
    }

    // A withheld job id is rewritten where it can be read as text: in the parsed string leaves of a JSON file (never a key or a
    // number, so `"percent": 100` survives an id of "1", and an escaped spelling is already decoded) and on token boundaries in
    // a log. A file that does not parse is omitted whole, so its raw text is never shipped (the leak scan still backstops).
    private static string? Copy(Loaded file, DiagnosticsRedactor redactor, JobIdWithholder ids)
    {
        var spec = SpecFor(file.Kind);
        if (file.Kind is FileKind.Status or FileKind.Result)
        {
            return file.Truncated ? "<omitted: file too large>" : redactor.RedactJson(file.Text, spec!, ids.FolderId(file.Path), ids.Rewrite, ids.ExactPlaceholder);
        }

        // A cut file starts mid-line: drop that partial line and say so.
        var text = file.Truncated ? file.Text[(file.Text.IndexOf('\n', StringComparison.Ordinal) + 1)..] : file.Text;
        var body = file.Kind == FileKind.Progress ? redactor.RedactJsonLines(text, spec!, ids.Rewrite, ids.ExactPlaceholder) : redactor.RedactText(ids.Rewrite(text));
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
        catch (Exception ex) when (DiagnosticsRedactor.IsUnreadableJson(ex))
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
    private static object ToHistoryRow(RunRecord run, string jobId) => new
    {
        jobId,
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

    // Only when the user ticked "include my notes and tags" (issue #141): the row plus those two fields, nothing else added.
    // The user chose to share the words, not a path, an address or a sequence pasted into them: each note and tag goes through the
    // redactor like a log line, and one that still trips the leak scan is replaced by a fixed placeholder, so a note can never make
    // the bundle fail (cold review of #141, issue #782).
    private static object WithNotesAndTags(object row, RunRecord run, DiagnosticsRedactor redactor, IEnumerable<string> scrub)
    {
        var node = JsonSerializer.SerializeToNode(row)!.AsObject();
        node["notes"] = string.IsNullOrWhiteSpace(run.Notes) ? null : Shareable(run.Notes, redactor, scrub);
        node["tags"] = JsonSerializer.SerializeToNode(RunTags.Parse(run.TagsJson).Select(tag => Shareable(tag, redactor, scrub)).ToList());
        return node;
    }

    private static string Shareable(string text, DiagnosticsRedactor redactor, IEnumerable<string> scrub)
    {
        var redacted = redactor.RedactText(text);
        var probe = new Dictionary<string, string> { ["note"] = redacted };
        return DiagnosticsLeakScan.Find(probe, scrub) is null ? redacted : OmittedNote;
    }

    private const string OmittedNote = "<omitted: note withheld>";

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
}

/// <summary>
/// A job id the app did not make (not the <c>yyyymmdd-hhmmss-xxxxxx</c> shape: an imported, hand-edited or older row) may hold
/// user text, so it is never shipped. It becomes <c>withheldid&lt;n&gt;</c> (n by run row, then by unmatched folder name), the
/// same word in the history and in entry names, and joins the scrub list so file text naming it is scrubbed too. A standard
/// id is returned as it is. MEASURED 2026-10-03 (issue #626): the owner's real run has a non-standard id.
/// </summary>
internal sealed class JobIdWithholder(ISet<string> scrub)
{
    private readonly Dictionary<string, string> _placeholders = new(StringComparer.OrdinalIgnoreCase);
    private Regex? _pattern;
    private string[] _alternativePlaceholders = [];
    private int _patternCount;

    /// <summary>What a blank id (an empty or whitespace-only row id) ships as. Never part of the rewrite pattern: it would match everywhere.</summary>
    private const string BlankPlaceholder = "withheldid0";

    public string Map(string id)
    {
        if (JobId.MatchesConvention(id))
        {
            return id;
        }

        if (string.IsNullOrWhiteSpace(id))
        {
            return BlankPlaceholder;
        }

        if (!_placeholders.TryGetValue(id, out var placeholder))
        {
            placeholder = $"withheldid{_placeholders.Count + 1}";
            _placeholders[id] = placeholder;
            scrub.Add(id);
        }

        return placeholder;
    }

    /// <summary>
    /// Replaces every withheld id in <paramref name="text"/> with its placeholder, case-insensitively and only as a whole token
    /// (not glued to a letter or digit on either side), whatever the id is: the scrub list drops an all-digit or 1-character
    /// value, a record number among them, and this does not. One pass, longest id first, so a placeholder is never rewritten
    /// again and an id inside a longer one loses to the longer one. A longer word that merely contains a short id ("quota"
    /// for "q") is left alone; the scrub list checks substrings of 3 or more characters.
    /// </summary>
    public string Rewrite(string text)
    {
        if (_placeholders.Count == 0)
        {
            return text;
        }

        if (_pattern is null || _patternCount != _placeholders.Count)
        {
            // One numbered group per id, longest first. The placeholder is found by WHICH group matched, never by looking the
            // matched text up: the regex folds case by Unicode rules (a Kelvin sign matches "k") and a dictionary does not.
            var ordered = _placeholders.OrderByDescending(pair => pair.Key.Length).ThenBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
            _alternativePlaceholders = [.. ordered.Select(pair => pair.Value)];
            // A lookaround guards an edge only when the id's own edge character is a letter or digit: an id of "#1" must be found
            // in "job#1" (nothing to be "glued" to at a symbol edge).
            _pattern = new Regex(
                string.Join('|', ordered.Select(pair => "(" + EdgeGuard(pair.Key[0], behind: true) + Regex.Escape(pair.Key) + EdgeGuard(pair.Key[^1], behind: false) + ")")),
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            _patternCount = _placeholders.Count;
        }

        return _pattern.Replace(text, match =>
        {
            for (var i = 0; i < _alternativePlaceholders.Length; i++)
            {
                if (match.Groups[i + 1].Success)
                {
                    return _alternativePlaceholders[i];
                }
            }

            // Cannot happen (every match is one group), but a miss must never ship the raw text.
            return "withheldid-unknown";
        });
    }

    private static string EdgeGuard(char edge, bool behind) =>
        char.IsLetterOrDigit(edge) ? (behind ? @"(?<![\p{L}\p{N}])" : @"(?![\p{L}\p{N}])") : string.Empty;

    /// <summary>
    /// The placeholder when <paramref name="text"/> is, as a whole and ordinally ignoring case, a withheld id; otherwise null.
    /// Used for a JSON number leaf, where a token match would split <c>0.1</c> at the dot.
    /// </summary>
    public string? ExactPlaceholder(string text) =>
        _placeholders.TryGetValue(text, out var placeholder) ? placeholder : null;

    /// <summary>The id a file under <c>runs/&lt;id&gt;/</c> belongs to, as shipped (the placeholder for a withheld one); null elsewhere.</summary>
    public string? FolderId(string path)
    {
        var parts = path.Split('/');
        return parts.Length >= 3 && parts[0] == "runs" ? Map(parts[1]) : null;
    }

    /// <summary>Replaces the job id segment of a <c>runs/&lt;id&gt;/...</c> path; any other path is returned unchanged.</summary>
    public string MapPath(string path)
    {
        var parts = path.Split('/');
        if (parts.Length >= 3 && parts[0] == "runs")
        {
            parts[1] = Map(parts[1]);
        }

        return string.Join('/', parts);
    }
}
