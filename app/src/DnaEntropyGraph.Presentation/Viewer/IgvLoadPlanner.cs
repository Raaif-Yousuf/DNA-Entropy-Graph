using System.Text.Json;
using DnaEntropyGraph.Core.Viewers;

namespace DnaEntropyGraph.Presentation.Viewer;

public enum IgvPlanFailure
{
    None,
    FolderMissing,
    NoSequenceFile,
    NoEntropyTrack,
    AmbiguousSequenceFile,
    SequenceTooLarge,
    IndexFailed,
}

/// <summary>What to show for one run folder: the JSON <c>load</c> command for bridge.js, or why there is none.</summary>
public sealed record IgvLoadPlan(IgvPlanFailure Failure, string? RunFolder, string? LoadMessageJson, string? FastaPath = null)
{
    /// <summary>Smoothed entropy tracks the run has (issue #552), by window. Not in the load command: the viewer adds one when the user switches it on, so the raw track stays primary.</summary>
    public IReadOnlyList<SmoothedTrackOption> SmoothedTracks { get; init; } = [];
}

/// <summary>One <c>&lt;name&gt;.entropy.smooth&lt;W&gt;.bedgraph</c> (or <c>.wig</c>) of the run: its window, igv.js format and URL on the run host.</summary>
public sealed record SmoothedTrackOption(int Window, string Format, string Url);

public static class ViewerLimits
{
    /// <summary>
    /// The largest FASTA the viewer loads. The reference is loaded with <c>indexed:false</c> (the worker writes no
    /// <c>.fai</c>), which makes igv.js read the whole file into the page. The spec puts the limit at "inputs above
    /// ~50 MB"; above it a <c>.fai</c> would be needed (docs/superpowers/specs Appendix A section 5, "Data feed").
    /// </summary>
    public const long MaxFastaBytes = 50L * 1024 * 1024;

    /// <summary>
    /// The largest FASTA the viewer loads through a generated <c>.fai</c> (issue #497): igv.js then reads it by byte range, so the size
    /// no longer decides memory, only how long the one-off index takes. Above this the file is refused as before (a multi-gigabyte
    /// genome is out of scope).
    /// </summary>
    public const long MaxIndexedFastaBytes = 1024L * 1024 * 1024;

    /// <summary>A limit in the words the user reads ("1 GB", "50 MB"), so the message is formatted from the constant and never drifts from it.</summary>
    public static string Describe(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 and var b when b % (1024L * 1024 * 1024) == 0 => $"{b / (1024L * 1024 * 1024)} GB",
        >= 1024L * 1024 and var b when b % (1024L * 1024) == 0 => $"{b / (1024L * 1024)} MB",
        _ => $"{bytes} bytes",
    };
}

/// <summary>
/// Builds the igv.js load command from a run folder's files (issue #73). File names follow
/// docs/science_and_formats.md: <c>&lt;name&gt;.fasta</c>, <c>&lt;name&gt;.entropy.bedgraph</c> (or <c>.wig</c>),
/// <c>&lt;name&gt;.genes.gff3</c>, and in "both, separate tracks" mode <c>.entropy.fwd.*</c> and <c>.entropy.rev.*</c>
/// "alongside the combined track" (so up to three entropy tracks). Entropy tracks are fixed to 0..2 bits (Hard Rule 3).
/// The sequence file is the one whose stem has an entropy file; several candidates or none are reported, never guessed.
/// </summary>
public static class IgvLoadPlanner
{
    private static readonly string[] EntropyExtensions = ["bedgraph", "wig"];

    /// <summary>
    /// Plans one run folder. A FASTA over <paramref name="maxFastaBytes"/> is refused (<see cref="IgvPlanFailure.SequenceTooLarge"/>, with its
    /// path in <see cref="IgvLoadPlan.FastaPath"/>) unless an <paramref name="indexStore"/> is given and the file is within
    /// <paramref name="maxIndexedBytes"/>: then its <c>.fai</c> is built (blocking, the caller runs this off the UI thread) in the store's own
    /// folder and the reference loads indexed (issue #497).
    /// </summary>
    public static IgvLoadPlan Plan(
        string? runFolder,
        long maxFastaBytes = ViewerLimits.MaxFastaBytes,
        IFastaIndexStore? indexStore = null,
        long maxIndexedBytes = ViewerLimits.MaxIndexedFastaBytes)
        => PlanCancellable(runFolder, maxFastaBytes, indexStore, maxIndexedBytes, CancellationToken.None);

    /// <summary><see cref="Plan"/> whose index build stops with <see cref="OperationCanceledException"/> when the page is left (issue #497).</summary>
    public static IgvLoadPlan PlanCancellable(
        string? runFolder,
        long maxFastaBytes,
        IFastaIndexStore? indexStore,
        long maxIndexedBytes,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(runFolder) || !Directory.Exists(runFolder))
        {
            return new IgvLoadPlan(IgvPlanFailure.FolderMissing, null, null);
        }

        var fastas = Directory.EnumerateFiles(runFolder, "*.fasta", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .OfType<string>()
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (fastas.Count == 0)
        {
            return Fail(IgvPlanFailure.NoSequenceFile, runFolder);
        }

        var withEntropy = fastas.Where(f => HasEntropy(runFolder, Path.GetFileNameWithoutExtension(f))).ToList();
        if (withEntropy.Count == 0)
        {
            return Fail(IgvPlanFailure.NoEntropyTrack, runFolder);
        }

        if (withEntropy.Count > 1)
        {
            return Fail(IgvPlanFailure.AmbiguousSequenceFile, runFolder);
        }

        var fasta = withEntropy[0];
        var fastaPath = Path.Combine(runFolder, fasta);
        var fastaBytes = new FileInfo(fastaPath).Length;
        string? indexUrl = null;
        if (fastaBytes > maxFastaBytes)
        {
            if (indexStore is null || fastaBytes > maxIndexedBytes)
            {
                return new IgvLoadPlan(IgvPlanFailure.SequenceTooLarge, runFolder, null, fastaPath);
            }

            try
            {
                indexUrl = ViewerUrls.IndexBaseUrl + Uri.EscapeDataString(Path.GetFileName(indexStore.EnsureIndex(fastaPath, cancellationToken)));
            }
            catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
            {
                return Fail(IgvPlanFailure.IndexFailed, runFolder);
            }
        }

        var stem = Path.GetFileNameWithoutExtension(fasta);
        var tracks = new List<object>();
        AddEntropyTrack(tracks, runFolder, $"{stem}.entropy", "Entropy (bits)");
        AddEntropyTrack(tracks, runFolder, $"{stem}.entropy.fwd", "Entropy, forward (bits)");
        AddEntropyTrack(tracks, runFolder, $"{stem}.entropy.rev", "Entropy, reverse complement (bits)");

        var genes = $"{stem}.genes.gff3";
        if (File.Exists(Path.Combine(runFolder, genes)))
        {
            tracks.Add(new Dictionary<string, object>
            {
                ["type"] = "annotation",
                ["format"] = "gff3",
                ["name"] = "Genes",
                ["url"] = Url(genes),
            });
        }

        AddRegionsTrack(tracks, runFolder, stem);

        var reference = new Dictionary<string, object>
        {
            ["id"] = stem,
            ["name"] = stem,
            ["fastaURL"] = Url(fasta),
            ["indexed"] = indexUrl is not null,
        };
        if (indexUrl is not null)
        {
            reference["indexURL"] = indexUrl;
        }

        var message = new Dictionary<string, object>
        {
            ["cmd"] = "load",
            ["config"] = new Dictionary<string, object>
            {
                ["reference"] = reference,
                ["tracks"] = tracks,
            },
        };

        return new IgvLoadPlan(IgvPlanFailure.None, runFolder, JsonSerializer.Serialize(message)) { SmoothedTracks = SmoothedTracksOf(runFolder, stem) };
    }

    // Issue #552: the region caller writes both; the BED is the plain one, so it wins and at most one track is drawn.
    private static void AddRegionsTrack(List<object> tracks, string folder, string stem)
    {
        foreach (var ext in new[] { "bed", "gff3" })
        {
            var file = $"{stem}.regions.{ext}";
            if (File.Exists(Path.Combine(folder, file)))
            {
                tracks.Add(new Dictionary<string, object>
                {
                    ["type"] = "annotation",
                    ["format"] = ext,
                    ["name"] = "Entropy regions",
                    ["url"] = Url(file),
                });
                return;
            }
        }
    }

    // A window is the digits between "smooth" and the extension; anything else that merely starts alike is not the worker's file.
    private static List<SmoothedTrackOption> SmoothedTracksOf(string folder, string stem)
    {
        var prefix = $"{stem}.entropy.smooth";
        var byWindow = new SortedDictionary<int, SmoothedTrackOption>();
        foreach (var ext in EntropyExtensions)
        {
            foreach (var path in Directory.EnumerateFiles(folder, prefix + "*." + ext, SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(path);
                var digits = name[prefix.Length..^(ext.Length + 1)];
                if (digits.Length > 0 && digits.All(char.IsAsciiDigit) && int.TryParse(digits, out var window) && window > 0)
                {
                    byWindow.TryAdd(window, new SmoothedTrackOption(window, ext, Url(name)));
                }
            }
        }

        return [.. byWindow.Values];
    }

    private static IgvLoadPlan Fail(IgvPlanFailure failure, string folder) => new(failure, folder, null);

    private static bool HasEntropy(string folder, string stem) =>
        new[] { $"{stem}.entropy", $"{stem}.entropy.fwd", $"{stem}.entropy.rev" }
            .Any(b => EntropyExtensions.Any(e => File.Exists(Path.Combine(folder, $"{b}.{e}"))));

    // bedGraph is preferred over WIG for each track; at most one file per track.
    private static void AddEntropyTrack(List<object> tracks, string folder, string baseName, string label)
    {
        foreach (var ext in EntropyExtensions)
        {
            var file = $"{baseName}.{ext}";
            if (!File.Exists(Path.Combine(folder, file)))
            {
                continue;
            }

            tracks.Add(new Dictionary<string, object>
            {
                ["type"] = "wig",
                ["format"] = ext,
                ["name"] = label,
                ["url"] = Url(file),
                ["min"] = 0,
                ["max"] = 2,
                ["autoscale"] = false,
            });
            return;
        }
    }

    private static string Url(string fileName) => ViewerUrls.RunBaseUrl + Uri.EscapeDataString(fileName);
}

/// <summary>
/// Decides whether one attach of the WebView2 control may proceed, and whether a pending attach is still wanted
/// after an <c>await</c> (the page may have been left meanwhile). A control that was closed is never attached again.
/// </summary>
public sealed class ViewerAttachGate
{
    private int _generation;
    private bool _begun;
    private bool _closed;

    public bool TryBegin(out int generation)
    {
        generation = _generation;
        if (_begun || _closed)
        {
            return false;
        }

        _begun = true;
        return true;
    }

    public bool IsCurrent(int generation) => !_closed && generation == _generation;

    public void Close()
    {
        _closed = true;
        _generation++;
    }
}
