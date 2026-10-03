using System.Text.Json;

namespace DnaEntropyGraph.Presentation.Viewer;

public enum IgvPlanFailure
{
    None,
    FolderMissing,
    NoSequenceFile,
    NoEntropyTrack,
    AmbiguousSequenceFile,
    SequenceTooLarge,
}

/// <summary>What to show for one run folder: the JSON <c>load</c> command for bridge.js, or why there is none.</summary>
public sealed record IgvLoadPlan(IgvPlanFailure Failure, string? RunFolder, string? LoadMessageJson);

public static class ViewerLimits
{
    /// <summary>
    /// The largest FASTA the viewer loads. The reference is loaded with <c>indexed:false</c> (the worker writes no
    /// <c>.fai</c>), which makes igv.js read the whole file into the page. The spec puts the limit at "inputs above
    /// ~50 MB"; above it a <c>.fai</c> would be needed (docs/superpowers/specs Appendix A section 5, "Data feed").
    /// </summary>
    public const long MaxFastaBytes = 50L * 1024 * 1024;
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

    public static IgvLoadPlan Plan(string? runFolder, long maxFastaBytes = ViewerLimits.MaxFastaBytes)
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
        if (new FileInfo(Path.Combine(runFolder, fasta)).Length > maxFastaBytes)
        {
            return Fail(IgvPlanFailure.SequenceTooLarge, runFolder);
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

        var message = new Dictionary<string, object>
        {
            ["cmd"] = "load",
            ["config"] = new Dictionary<string, object>
            {
                ["reference"] = new Dictionary<string, object>
                {
                    ["id"] = stem,
                    ["name"] = stem,
                    ["fastaURL"] = Url(fasta),
                    ["indexed"] = false,
                },
                ["tracks"] = tracks,
            },
        };

        return new IgvLoadPlan(IgvPlanFailure.None, runFolder, JsonSerializer.Serialize(message));
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
