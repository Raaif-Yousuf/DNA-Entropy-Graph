using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DnaEntropyGraph.Core.Inputs;

namespace DnaEntropyGraph.Core.Contract;

/// <summary>
/// One input file staged for a run: where the app's own copy lives
/// (<see cref="LocalPath"/>, under app data, Hard Rule 14) and the plain file
/// name it is uploaded under (<c>jobs/&lt;jobId&gt;/input/&lt;FileName&gt;</c>).
/// </summary>
/// <param name="IsAnnotation">True for a GFF3 paired with the input listed before it (issue #686): it is uploaded like any input but is not an input of its own; the manifest names it in that input's <c>annotation</c>.</param>
public sealed record StagedInput(string LocalPath, string FileName, bool IsAnnotation = false)
{
    /// <summary>The path inside the job prefix, as <c>manifest.json</c> names it.</summary>
    public string ObjectName => "input/" + FileName;
}

/// <summary>
/// Issue #460: writes <c>manifest.json</c> (docs/job_contract.md section 3,
/// schema <c>docs/contract/manifest.schema.json</c>) from what the user chose.
/// Every value is the exact wire spelling the worker parses; where the worker
/// cross-checks two fields (stride against window and context, precision
/// against the model) this derives them so the pair cannot disagree.
/// </summary>
public static class WorkerManifestBuilder
{
    // Defaults the worker documents for limits.* (job_contract.md section 3, "Field notes").
    private const int CancelPollSeconds = 10;
    /// <summary>The <c>limits.heartbeatSeconds</c> the manifest asks for; the runner derives its staleness limit from it.</summary>
    public const int HeartbeatSeconds = 30;
    private const int MaxInputs = 50;
    private const int MaxTotalNt = 20_000_000;

    /// <summary>
    /// THEORY (unverified): the models whose worker-side requirement is fp8, copied from
    /// <c>worker/src/dna_entropy/predictors/hardware.py</c> <c>MODEL_REQUIREMENTS</c>. The
    /// worker refuses a manifest whose precision disagrees with its own table, so this list
    /// must follow that one; an unlisted model is bf16, the worker's own default.
    /// </summary>
    private static readonly HashSet<string> Fp8Models = new(StringComparer.Ordinal) { "evo2_1b_base", "evo2_20b", "evo2_40b" };

    /// <summary>True for a model whose worker-side requirement is fp8 (Hopper only), so no PC with an ordinary GPU can run it (issue #173).</summary>
    public static bool NeedsFp8(string modelId) => Fp8Models.Contains(modelId);

    /// <summary>The manifest as compact JSON. Throws <see cref="ArgumentException"/> for a request the worker would misread (no inputs, an empty output selection, a file name that could leave the job prefix).</summary>
    public static string Build(
        RunOptions options,
        string jobId,
        string installationId,
        string appVersion,
        string? workerImage,
        string bucket,
        IReadOnlyList<StagedInput> inputs,
        DateTimeOffset createdAt,
        string? localRoot = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.Count == 0)
        {
            throw new ArgumentException("A manifest needs at least one input.", nameof(inputs));
        }

        var inputEntries = new JsonArray();
        JsonObject? last = null;
        for (var i = 0; i < inputs.Count; i++)
        {
            RequireSafeFileName(inputs[i].FileName);
            if (inputs[i].IsAnnotation)
            {
                // Issue #686: a GFF3 rides on the input before it (worker manifest.py: inputs[].annotation, an object name like path).
                if (last is null || last.ContainsKey("annotation"))
                {
                    throw new ArgumentException("A GFF3 annotation needs exactly one input before it to pair with.", nameof(inputs));
                }

                last["annotation"] = inputs[i].ObjectName;
                continue;
            }

            last = new JsonObject
            {
                ["id"] = "in" + (inputEntries.Count + 1).ToString(CultureInfo.InvariantCulture),
                ["path"] = inputs[i].ObjectName,
                ["name"] = Path.GetFileNameWithoutExtension(inputs[i].FileName),
                ["informat"] = InFormat(options.Format),
                ["start"] = options.StartCoordinate,
                ["rna"] = options.TreatAsRna,
                ["genes"] = options.FindGenes,
                ["ambiguityPolicy"] = options.AmbiguityPolicy.ToWireValue(),
                ["fastaRecords"] = options.FastaRecords == FastaRecordsSelection.FirstOnly ? "first" : "all",
            };
            inputEntries.Add(last);
        }

        if (inputEntries.Count == 0)
        {
            throw new ArgumentException("A manifest needs at least one input besides an annotation.", nameof(inputs));
        }

        var (window, stride) = WindowAndStride(options);
        var mock = options.Predictor == PredictorSelection.MockTestPredictor;

        var worker = new JsonObject { ["version"] = appVersion };
        if (workerImage is not null)
        {
            worker["image"] = workerImage;
        }

        var root = new JsonObject
        {
            ["schema"] = 1,
            ["jobId"] = jobId,
            ["createdAt"] = createdAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            // accountSub is deliberately never written: the bucket must not carry the account id.
            ["createdBy"] = new JsonObject { ["installationId"] = installationId, ["appVersion"] = appVersion },
            ["worker"] = worker,
            ["inputs"] = inputEntries,
            ["predictor"] = new JsonObject
            {
                ["kind"] = mock ? "mock" : "evo",
                ["model"] = options.ModelId,
                ["precision"] = Fp8Models.Contains(options.ModelId) ? "fp8" : "bf16",
                ["device"] = mock ? "cpu" : "cuda",
                ["seed"] = options.Seed,
            },
            ["analysis"] = new JsonObject
            {
                ["contextLength"] = options.ContextLength,
                ["window"] = window,
                ["stride"] = stride,
                ["direction"] = DirectionWire(options.Direction),
                ["format"] = options.TrackFormat == TrackFormat.Wig ? "wig" : "bedgraph",
            },
            ["outputs"] = Outputs(options),
            ["limits"] = new JsonObject
            {
                ["maxRunSeconds"] = options.MaxRunDurationMinutes * 60,
                ["cancelPollSeconds"] = CancelPollSeconds,
                ["heartbeatSeconds"] = HeartbeatSeconds,
                ["maxInputs"] = MaxInputs,
                ["maxTotalNt"] = MaxTotalNt,
            },
            ["lifecycle"] = new JsonObject
            {
                ["afterTask"] = options.AfterTask switch
                {
                    AfterTaskAction.Delete => "delete",
                    AfterTaskAction.KeepAlive => "keep",
                    _ => "stop",
                },
                ["keepAliveMinutes"] = options.KeepAliveMinutes,
                ["afterKeepAlive"] = options.AfterKeepAlive == AfterKeepAliveAction.Delete ? "delete" : "stop",
            },
            // A This PC run (issue #174) is the same manifest with the store a folder: docs/job_contract.md section 3.
            ["store"] = localRoot is null
                ? new JsonObject
                {
                    ["kind"] = "gcs",
                    ["bucket"] = bucket,
                    ["prefix"] = JobPrefix(jobId),
                }
                : new JsonObject
                {
                    ["kind"] = "localdir",
                    ["root"] = localRoot,
                },
        };

        if (options.Topology != Topology.Auto)
        {
            // Optional on the wire (job_contract.md section 3): an older manifest without it means "auto", so auto is never sent.
            root["analysis"]!["topology"] = options.Topology == Topology.Circular ? "circular" : "linear";
        }

        if (options.OutputFiles.HasFlag(OutputFileKinds.Gc))
        {
            // Read only when "gc" is named (job_contract.md section 3, issue #688); sending it otherwise would be noise.
            root["analysis"]!["gcWindow"] = options.GcWindow;
        }

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    /// <summary>The prefix every object of one job lives under (job_contract.md section 1).</summary>
    public static string JobPrefix(string jobId) => "jobs/" + jobId + "/";

    private static void RequireSafeFileName(string name)
    {
        var bad = string.IsNullOrWhiteSpace(name)
                  || name is "." or ".."
                  || name.Contains('/', StringComparison.Ordinal)
                  || name.Contains('\\', StringComparison.Ordinal);
        if (bad)
        {
            throw new ArgumentException("An input file name must be a plain name with no folders.", nameof(name));
        }
    }

    /// <summary>
    /// The worker refuses a manifest unless <c>stride == window - contextLength</c>, and the
    /// contract says window and stride are derived, not chosen. Use the options' window when it
    /// leaves a positive stride, otherwise the contract's own derivation (window = 2K).
    /// </summary>
    private static (int Window, int Stride) WindowAndStride(RunOptions options)
    {
        var k = options.ContextLength;
        var window = options.Window;
        return window - k >= 1 ? (window, window - k) : (2 * k, k);
    }

    private static string InFormat(InputFormat format) => format switch
    {
        InputFormat.GenBank => "genbank",
        InputFormat.Fasta => "fasta",
        InputFormat.Plain => "paste",
        InputFormat.Embl => "embl",
        InputFormat.SnapGene => "snapgene",
        _ => "auto",
    };

    private static string DirectionWire(Direction direction) => direction switch
    {
        Direction.BothAveraged => "both-averaged",
        Direction.BothSeparate => "both-separate",
        Direction.ForwardOnly => "forward-only",
        Direction.ReverseOnly => "reverse-only",
        _ => "both-combined",
    };

    /// <summary>
    /// <c>outputs</c> is read literally by the worker, and an EMPTY array means "everything", so
    /// an empty selection must never be sent. When both track formats are ticked only the
    /// chosen <see cref="RunOptions.TrackFormat"/> is listed, so the worker cannot pick the other.
    /// </summary>
    private static JsonArray Outputs(RunOptions options)
    {
        var kinds = options.OutputFiles;
        if (kinds == OutputFileKinds.None)
        {
            throw new ArgumentException("Choose at least one output file.", nameof(options));
        }

        // The worker runs gene finding when "genes_gff3" is in outputs, whatever input.genes says
        // (worker manifest.py: genes = input.genes or "genes_gff3" in outputs). With gene finding
        // off the file must never be requested, or "All outputs" would switch genes on. "gene_summary" needs
        // genes the same way (it is written only when the run has some), so every catalog entry that
        // needs genes is held back together.
        if (!options.FindGenes)
        {
            foreach (var entry in OutputCatalog.Entries.Where(e => e.NeedsGenes))
            {
                kinds &= ~entry.Kind;
            }

            if (kinds == OutputFileKinds.None)
            {
                throw new ArgumentException("Choose at least one output file besides the gene files when gene finding is off.", nameof(options));
            }
        }

        if (kinds.HasFlag(OutputFileKinds.BedGraph) && kinds.HasFlag(OutputFileKinds.Wig))
        {
            kinds &= options.TrackFormat == TrackFormat.Wig ? ~OutputFileKinds.BedGraph : ~OutputFileKinds.Wig;
        }

        var names = new JsonArray();
        foreach (var wire in OutputCatalog.ToWireNames(kinds))
        {
            names.Add(wire);
        }

        if (names.Count == 0)
        {
            throw new ArgumentException("Choose at least one output file.", nameof(options));
        }

        return names;
    }
}
