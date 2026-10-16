using System.Collections.Concurrent;
using System.Text.Json;

namespace DnaEntropyGraph.Core.Tracks;

/// <summary>Finds a run's entropy track in its output folder and reads it for the Results overview and the Runs sparklines (issue #118).</summary>
public interface IEntropyOverviewSource
{
    /// <summary>The overview, or null when the folder has no raw entropy track or it cannot be read. Touches the disk: callers run it off the UI thread.</summary>
    EntropyOverview? Load(string folder);
}

/// <summary>
/// The real <see cref="IEntropyOverviewSource"/>. The track is the raw <c>*.entropy.bedgraph</c> (not a smoothed, forward or
/// reverse variant, whose names carry one more dot-part), and a linked file is never followed (Hard Rule 14: we only read the
/// run's own folder). The seam of each contig comes from <c>provenance.json</c>; a missing or broken file costs only the seam.
/// Results are remembered per file and last-write time, so opening the Runs page again does not re-read every run.
/// </summary>
public sealed class EntropyOverviewSource : IEntropyOverviewSource
{
    private const string TrackSuffix = ".entropy.bedgraph";
    private const int MaxRemembered = 64;

    private readonly ConcurrentDictionary<(string Path, long Ticks, long Bytes), EntropyOverview?> _remembered = new();

    public EntropyOverview? Load(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return null;
        }

        string? track;
        try
        {
            // A run's files sit one folder down (<run>/<name>/<name>.entropy.bedgraph); a link, hidden or system entry is never entered.
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                MaxRecursionDepth = 4,
                AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System,
            };
            track = Directory.EnumerateFiles(folder, "*" + TrackSuffix, options)
                .OrderBy(p => p.Length)
                .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (track is null)
        {
            return null;
        }

        var info = new FileInfo(track);
        var key = (track, info.LastWriteTimeUtc.Ticks, info.Length);
        if (_remembered.TryGetValue(key, out var known))
        {
            return known;
        }

        var overview = BedGraphReader.Read(track);
        if (overview is not null)
        {
            overview = WithSeams(overview, Path.Combine(Path.GetDirectoryName(track)!, "provenance.json"));
        }

        if (_remembered.Count >= MaxRemembered)
        {
            _remembered.Clear();
        }

        _remembered[key] = overview;
        return overview;
    }

    private static EntropyOverview WithSeams(EntropyOverview overview, string provenancePath)
    {
        var seams = ReadSeams(provenancePath);
        if (seams.Count == 0)
        {
            return overview;
        }

        return new EntropyOverview([.. overview.Contigs.Select(c => seams.TryGetValue(c.Name, out var seam) ? c with { Seam = seam } : c)]);
    }

    private static Dictionary<string, int> ReadSeams(string path)
    {
        var seams = new Dictionary<string, int>(StringComparer.Ordinal);
        try
        {
            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("contigs", out var contigs)
                && contigs.ValueKind == JsonValueKind.Array)
            {
                foreach (var contig in contigs.EnumerateArray())
                {
                    if (contig.ValueKind == JsonValueKind.Object
                        && contig.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                        && contig.TryGetProperty("seam", out var seam) && seam.ValueKind == JsonValueKind.Number && seam.TryGetInt32(out var value))
                    {
                        seams[name.GetString()!] = value;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // No seam is drawn; the chart is still right.
        }

        return seams;
    }
}
