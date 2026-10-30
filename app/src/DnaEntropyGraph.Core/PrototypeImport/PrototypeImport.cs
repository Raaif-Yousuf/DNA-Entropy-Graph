using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.LocalEngine;

namespace DnaEntropyGraph.Core.PrototypeImport;

/// <summary>Where to look for a prototype install: its config file and the folder its runs went to. The app supplies the real ones; a test supplies a temp folder.</summary>
public sealed record PrototypeSearchPaths(string ConfigPath, string Downloads);

/// <summary>One run folder the command-line prototype left in Downloads.</summary>
public sealed record LegacyRun(string Name, string Folder, DateTimeOffset ModifiedUtc);

/// <summary>What an earlier prototype install left on this PC (issue #243). <see cref="LastZone"/> and <see cref="SshKeyFile"/> are shown, never acted on.</summary>
public sealed record PrototypeScan(IReadOnlyList<LegacyRun> Runs, string? LastZone, string? SshKeyFile)
{
    public bool Found => Runs.Count > 0 || LastZone is not null || SshKeyFile is not null;
}

/// <summary>
/// Where the prototype kept things. MEASURED 2026-10-16 from the prototype's own code, not from the issue text:
/// worker/legacy/cloud/orchestrator.py (<c>_state_path</c>) keeps <c>%APPDATA%\dna-entropy\config.json</c> with <c>last_zone</c> and <c>ssh_key_file</c>,
/// and worker/legacy/dna-entropy-v1/cli.py writes each run to <c>Path.home() / "Downloads" / &lt;name&gt;</c> (the user-profile Downloads folder, not
/// the Windows known folder, which a user may have moved), with the track named <c>&lt;name&gt;.entropy.bedgraph</c>.
/// </summary>
public static class PrototypeLocations
{
    public static string ConfigPath(string appData) => Path.Combine(appData, "dna-entropy", "config.json");

    public static string Downloads(string userProfile) => Path.Combine(userProfile, "Downloads");
}

/// <summary>Finds a prototype install. Only reads (Hard Rule 14): nothing is written, moved or deleted, and nothing here can throw for a missing or odd file.</summary>
public static partial class PrototypeScanner
{
    /// <summary>A Downloads folder with thousands of sub-folders must not stall the app; the first this many are offered.</summary>
    public const int MaxRuns = 500;

    private const string TrackSuffix = ".entropy.bedgraph";
    private const long MaxConfigBytes = 64 * 1024;

    [GeneratedRegex("^[a-z][a-z0-9]*(-[a-z0-9]+)+$")]
    private static partial Regex ZoneId();

    public static PrototypeScan Scan(string configPath, string downloadsFolder)
    {
        var (zone, key) = ReadConfig(configPath);
        return new PrototypeScan(FindRuns(downloadsFolder), zone, key);
    }

    private static List<LegacyRun> FindRuns(string downloads)
    {
        var runs = new List<LegacyRun>();
        try
        {
            if (!Directory.Exists(downloads))
            {
                return runs;
            }

            foreach (var folder in Directory.EnumerateDirectories(downloads))
            {
                if (runs.Count >= MaxRuns)
                {
                    break;
                }

                try
                {
                    var attributes = File.GetAttributes(folder);
                    if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        continue;
                    }

                    // This app's own runs sit in Downloads too, and always carry provenance.json; the prototype never wrote one.
                    if (File.Exists(Path.Combine(folder, "provenance.json")))
                    {
                        continue;
                    }

                    var name = Path.GetFileName(folder);
                    var track = Path.Combine(folder, name + TrackSuffix);
                    if (File.Exists(track))
                    {
                        runs.Add(new LegacyRun(name, Path.GetFullPath(folder), new DateTimeOffset(File.GetLastWriteTimeUtc(track), TimeSpan.Zero)));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // One unreadable folder is skipped; the rest are still offered.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable Downloads folder finds nothing.
        }

        return runs;
    }

    private static (string? Zone, string? Key) ReadConfig(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > MaxConfigBytes)
            {
                return (null, null);
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            var zone = Text(document.RootElement, "last_zone");
            var key = Text(document.RootElement, "ssh_key_file");
            return (zone is not null && ZoneId().IsMatch(zone) && zone.Length <= 40 ? zone : null, key is not null && key.Length <= 260 && !key.Any(char.IsControl) ? key : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return (null, null);
        }
    }

    private static string? Text(JsonElement owner, string name)
        => owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()!.Trim() : null;
}

/// <summary>Adds a history row for each prototype run (issue #243). The row is finished, flagged <see cref="RunRecord.Imported"/>, and has no bucket, VM or cost, so every cloud action is off for it; Open uses the folder in Downloads where it already is.</summary>
public sealed class PrototypeImporter(IRunRepository runs, Runs.ImportedRunDismissals? dismissals = null)
{
    /// <summary>The same folder always gets the same id, so importing twice cannot add a run twice.</summary>
    public static string JobIdFor(string folder)
    {
        var key = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)).ToUpperInvariant();
        return "legacy-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..12].ToLowerInvariant();
    }

    /// <summary>The scanned runs that are not in history yet and were not removed by the user: not under their own id, and not under another id whose output folder is the same.</summary>
    public async Task<IReadOnlyList<LegacyRun>> NewRunsAsync(PrototypeScan scan, CancellationToken cancellationToken)
    {
        var existing = await runs.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var ids = existing.Select(r => r.JobId).ToHashSet(StringComparer.Ordinal);
        var folders = existing.Where(r => !string.IsNullOrWhiteSpace(r.OutputDir)).Select(r => Normalize(r.OutputDir!)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Issue #915: a row the user removed is remembered by its folder-derived id (the same setting a removed bucket row uses; the "legacy-" ids cannot collide with a job id).
        var dismissed = dismissals?.Load() ?? new HashSet<string>();
        return scan.Runs.Where(run => !ids.Contains(JobIdFor(run.Folder)) && !dismissed.Contains(JobIdFor(run.Folder)) && !folders.Contains(Normalize(run.Folder))).ToList();
    }

    /// <summary>Returns how many rows were added. A folder already in history, under any id, is left as it is.</summary>
    public async Task<int> ImportAsync(PrototypeScan scan, CancellationToken cancellationToken)
    {
        var added = 0;
        foreach (var run in await NewRunsAsync(scan, cancellationToken).ConfigureAwait(false))
        {
            await runs.UpsertAsync(
                new RunRecord(JobIdFor(run.Folder), JobPhase.Completed, run.ModifiedUtc, run.Name, Target: RunTargets.LocalRecord, OutputDir: run.Folder, FinishedAt: run.ModifiedUtc, Imported: true),
                cancellationToken).ConfigureAwait(false);
            added++;
        }

        return added;
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
