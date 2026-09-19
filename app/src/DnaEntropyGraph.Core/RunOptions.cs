using DnaEntropyGraph.Core.Inputs;

namespace DnaEntropyGraph.Core;

/// <summary>
/// Every user-chosen option for one run (issue #65; spec section 4.3's "Complete option
/// list (RunOptions)" table, cross-checked against what the worker's manifest can actually
/// honour: <c>worker/src/dna_entropy/worker/manifest.py</c> and
/// <c>docs/contract/manifest.json</c> - so an option the worker cannot honour never reaches
/// the UI). Serialised into <c>Runs.OptionsJson</c> and <c>manifest.json</c>'s corresponding
/// fields (see each property's doc comment for its wire name).
///
/// <b>Every property below has a default</b> so every existing construction site (the App,
/// Cloud, Presentation and LocalEngine projects owned by other lanes) keeps compiling
/// without change. <see cref="ModelId" />, <see cref="RunTarget" /> and
/// <see cref="OutputFolder" /> are the pre-existing three properties and are unchanged in
/// name, type or requiredness.
///
/// The spec's "Budget (Settings)" row (monthly warning cap, hard stop, delete-idle-days) is
/// deliberately NOT here: those are app-wide settings in <c>settings.json</c>
/// (<c>ISettingsStore</c>), not a per-run choice, per the row's own "(Settings)" label and
/// Appendix A's <c>Key classes</c> table (RunOptions serialises into <c>Runs.OptionsJson</c>
/// only). <see cref="Window" />/<see cref="Stride" /> are recorded, not user-chosen -
/// <c>GpuPlanner</c>/<c>ModelGpuLinker</c> (a different issue) compute the real values from
/// <see cref="ContextLength" /> and the chosen GPU's ceiling before a run starts; the
/// defaults here match the worker's own dataclass defaults so an untouched RunOptions still
/// serialises to a manifest the worker accepts.
/// </summary>
public sealed record RunOptions
{
    // ---- Pre-existing (Hard Rule: paths-you-own contract - do not rename, retype or remove) ----

    /// <summary>Spec 4.3 "Model" row wire id (e.g. "evo2_7b"). Manifest: <c>predictor.model</c>.</summary>
    public required string ModelId { get; init; }

    /// <summary>Spec 4.3 "Where to run" row: "Cloud" / "This PC" / "Auto". Not sent to the worker; consumed by <c>RunTargetResolver</c>.</summary>
    public required string RunTarget { get; init; }

    /// <summary>Spec 4.3 "Folder" row. Null =&gt; the app's default (Downloads).</summary>
    public string? OutputFolder { get; init; }

    // ---- Input ----

    /// <summary>Manifest: <c>inputs[].informat</c>. Default "Auto" (auto-detect, matching <see cref="Inputs.SequenceSniffer" />).</summary>
    public InputFormat Format { get; init; } = InputFormat.Auto;

    /// <summary>Manifest: <c>inputs[].rna</c>.</summary>
    public bool TreatAsRna { get; init; }

    /// <summary>Manifest: <c>inputs[].start</c>. Must be &gt;= 1.</summary>
    public int StartCoordinate { get; init; } = 1;

    /// <summary>
    /// Manifest: <c>inputs[].fastaRecords</c>. Default <see cref="FastaRecordsSelection.All" />
    /// (design D14: a multi-record FASTA processes every record; "First only" reproduces the
    /// prototype's original single-record behaviour).
    /// </summary>
    public FastaRecordsSelection FastaRecords { get; init; } = FastaRecordsSelection.All;

    /// <summary>
    /// What to do with an IUPAC ambiguity code. Manifest: <c>inputs[].ambiguityPolicy</c>.
    /// Default <see cref="Inputs.AmbiguityPolicy.Keep" />, matching <c>manifest.py</c>'s
    /// <c>InputSpec.ambiguity_policy</c> default - NOT the same as
    /// <see cref="Inputs.SequenceValidator" />'s own strict-by-default function signature,
    /// which a plain "validate this file" preflight call should pass explicitly.
    /// </summary>
    public AmbiguityPolicy AmbiguityPolicy { get; init; } = AmbiguityPolicy.Keep;

    // ---- Model and GPU ----

    /// <summary>
    /// Linked to <see cref="ModelId" /> by <c>ModelGpuLinker</c> over
    /// <c>docs/contract/gpu-catalog.json</c> (not this record's job to enforce). Default
    /// <see cref="GpuTier.CheapestAvailable" /> (= L4 for the default 7B model).
    /// </summary>
    public GpuTier GpuTier { get; init; } = GpuTier.CheapestAvailable;

    /// <summary>Manifest: <c>predictor.kind</c> ("evo" / "mock"). Default <see cref="PredictorSelection.Evo2" />.</summary>
    public PredictorSelection Predictor { get; init; } = PredictorSelection.Evo2;

    /// <summary>Manifest: <c>predictor.seed</c>. Meaningful for the mock predictor only.</summary>
    public int Seed { get; init; }

    /// <summary>
    /// K: the amount of sequence the model must have seen before a prediction is trusted.
    /// Manifest: <c>analysis.contextLength</c>. Default 4,096, matching the worker's
    /// <c>DEFAULT_CONTEXT_LENGTH</c>.
    /// </summary>
    public int ContextLength { get; init; } = 4096;

    /// <summary>How forward and reverse-complement predictions combine. Manifest: <c>analysis.direction</c>.</summary>
    public Direction Direction { get; init; } = Direction.BothCombined;

    /// <summary>
    /// W = min(2K, GPU ceiling): derived, not user-chosen - recorded here only so a
    /// serialised manifest always carries a value even before <c>GpuPlanner</c> runs.
    /// Manifest: <c>analysis.window</c>. Matches the worker dataclass default (8,192).
    /// </summary>
    public int Window { get; init; } = 8192;

    /// <summary>S = W - K: derived, not user-chosen (see <see cref="Window" />). Manifest: <c>analysis.stride</c>.</summary>
    public int Stride { get; init; } = 4096;

    // ---- Genes ----

    /// <summary>
    /// Find genes (prokaryotic annotation via Prodigal; FASTA/paste input only - a GenBank
    /// input already carries its own genes, Hard Rule "use its genes as-is"). Manifest:
    /// <c>inputs[].genes</c>. Default On.
    /// </summary>
    public bool FindGenes { get; init; } = true;

    // ---- Output ----

    /// <summary>Which output files to write. Manifest: <c>outputs</c> (an empty/omitted array there means "everything", matching this default of every flag set).</summary>
    public OutputFileKinds OutputFiles { get; init; } = OutputFileKinds.All;

    /// <summary>
    /// The primary track's on-disk format when both are possible. Manifest:
    /// <c>analysis.format</c> ("bedgraph" / "wig"). Only meaningful when
    /// <see cref="OutputFileKinds.BedGraph" /> or <see cref="OutputFileKinds.Wig" /> is set
    /// in <see cref="OutputFiles" />.
    /// </summary>
    public TrackFormat TrackFormat { get; init; } = TrackFormat.BedGraph;

    /// <summary>Tokens: <c>{file} {date:yyyy-MM-dd} {time:HHmm} {model} {n}</c>. A name colliding with an existing local folder gets <c>_2</c> appended by <c>RunNamer</c>.</summary>
    public string NameTemplate { get; init; } = "{file}";

    // ---- Cloud ----

    /// <summary>Null =&gt; Auto (last-good zone, then the bucket's region, then a quota-filtered global list); otherwise an explicit zone id (e.g. "us-central1-a").</summary>
    public string? ZonePreference { get; init; }

    /// <summary>
    /// DECISION (agent-made in the original spec pass, reversible): off by default. Spot
    /// VMs auto-fall back to on-demand after one preemption when this is on.
    /// </summary>
    public bool SpotVm { get; init; }

    /// <summary>What the worker does to its own VM once every output is uploaded. Manifest: <c>lifecycle.afterTask</c>. Default Stop (Hard Rule 11).</summary>
    public AfterTaskAction AfterTask { get; init; } = AfterTaskAction.Stop;

    /// <summary>Manifest: <c>lifecycle.keepAliveMinutes</c>. Only consulted when <see cref="AfterTask" /> is <see cref="AfterTaskAction.KeepAlive" />. Range 5..240 per spec.</summary>
    public int KeepAliveMinutes { get; init; } = 30;

    /// <summary>What happens once the keep-alive window above expires. Manifest: <c>lifecycle.afterKeepAlive</c>. "Keep alive always has an expiry" (Hard Rule 11) - this enum has no keep-forever value on purpose.</summary>
    public AfterKeepAliveAction AfterKeepAlive { get; init; } = AfterKeepAliveAction.Stop;

    /// <summary>The VM's own safety-cap backstop (Hard Rule 10), independent of the app's own polling. Manifest: <c>limits.maxRunSeconds</c> is derived from this (default 240 min = 4 h = 14,400 s, matching the worker's own default).</summary>
    public int MaxRunDurationMinutes { get; init; } = 240;

    /// <summary>How long cloud-side outputs are kept before the bucket's own lifecycle rule deletes them. Not sent to the worker; consumed by bucket provisioning.</summary>
    public int CloudResultsRetentionDays { get; init; } = 90;

    /// <summary>Boot disk size for the VM. Not sent to the worker manifest; consumed by <c>VmSpec</c>.</summary>
    public int BootDiskGb { get; init; } = 150;

    // ---- Local / notifications (Appendix A section 2.3's "Advanced - Output and notifications", finer-grained than spec 4.3's 3-item "Local" row) ----

    /// <summary>Open the igv.js viewer automatically once a run finishes. Default On (spec 4.3's "open results").</summary>
    public bool OpenViewerWhenDone { get; init; } = true;

    /// <summary>Show a toast notification once a run finishes. Default On (spec 4.3's "Notify when done").</summary>
    public bool ToastWhenDone { get; init; } = true;

    /// <summary>Open the output folder in Explorer once a run finishes. Default Off.</summary>
    public bool OpenFolderWhenDone { get; init; }

    /// <summary>Play a sound once a run finishes. Default Off.</summary>
    public bool PlaySoundWhenDone { get; init; }

    /// <summary>
    /// Copy the worker log into the output folder as well as the app-data copy that is
    /// always kept. Default Off - the log is always kept under
    /// <c>%LOCALAPPDATA%\DNAEntropyGraph\logs\</c> regardless of this flag.
    /// </summary>
    public bool KeepWorkerLogInOutputFolder { get; init; }
}

/// <summary>Spec 4.3 "Input / Format" row. Wire name: <c>inputs[].informat</c> ("auto" | "genbank" | "fasta" | "paste").</summary>
public enum InputFormat
{
    Auto,
    GenBank,
    Fasta,
    Plain,
}

/// <summary>Spec 4.3 "Input / FASTA records" row. Wire name: <c>inputs[].fastaRecords</c> ("all" | "first").</summary>
public enum FastaRecordsSelection
{
    All,
    FirstOnly,
}

/// <summary>Spec 4.3 "Model / GPU tier" row. Not sent to the worker manifest directly; resolved to a machine type/accelerator pair by <c>GpuPlanner</c>.</summary>
public enum GpuTier
{
    CheapestAvailable,
    L4,
    A100_40,
    A100_80,
    H100,
}

/// <summary>Spec 4.3 "Model / Predictor" row. Wire name: <c>predictor.kind</c> ("evo" | "mock").</summary>
public enum PredictorSelection
{
    Evo2,
    MockTestPredictor,
}

/// <summary>Spec 4.3 "Model / Direction" row. Wire name: <c>analysis.direction</c>.</summary>
public enum Direction
{
    BothCombined,
    BothAveraged,
    BothSeparate,
    ForwardOnly,
    ReverseOnly,
}

/// <summary>Spec 4.3 "Output / Files" row. Wire name: <c>outputs</c> array membership (see <c>docs/job_contract.md</c> section 3's vocabulary).</summary>
[Flags]
public enum OutputFileKinds
{
    None = 0,
    GenBank = 1 << 0,
    Fasta = 1 << 1,
    BedGraph = 1 << 2,
    Wig = 1 << 3,
    GeneiousGff3 = 1 << 4,
    GenesGff3 = 1 << 5,
    Stats = 1 << 6,
    EntropyTsv = 1 << 7,
    All = GenBank | Fasta | BedGraph | Wig | GeneiousGff3 | GenesGff3 | Stats | EntropyTsv,
}

/// <summary>Track file format when both are possible. Wire name: <c>analysis.format</c>.</summary>
public enum TrackFormat
{
    BedGraph,
    Wig,
}

/// <summary>Spec 4.3 "Cloud / After task" row. Wire name: <c>lifecycle.afterTask</c> ("stop" | "delete" | "keep").</summary>
public enum AfterTaskAction
{
    Stop,
    Delete,
    KeepAlive,
}

/// <summary>Spec 4.3 "Cloud / Keep-alive minutes, then" row. Wire name: <c>lifecycle.afterKeepAlive</c> ("stop" | "delete") - intentionally has no "keep forever" value (Hard Rule 11).</summary>
public enum AfterKeepAliveAction
{
    Stop,
    Delete,
}
