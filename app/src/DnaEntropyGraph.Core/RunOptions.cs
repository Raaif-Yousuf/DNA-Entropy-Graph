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

    /// <summary>
    /// The file the user dropped or browsed to (issue #460). Read-only to the app: JobEngine copies it
    /// under app data and the copy is what is uploaded (Hard Rule 14). Null means "no input chosen"
    /// and a run with none fails before any cloud resource exists.
    /// </summary>
    public string? InputPath { get; init; }

    /// <summary>
    /// A GFF3 paired with <see cref="InputPath"/> (a FASTA file; issue #686). Like the input it is read-only to the app: JobEngine copies it under
    /// app data and the copy is uploaded. Manifest: <c>inputs[].annotation</c> (the uploaded object's name). Null means none: the worker finds genes itself.
    /// </summary>
    public string? AnnotationPath { get; init; }

    /// <summary>
    /// An NCBI accession the worker fetches on the VM instead of a file (issue #684), already checked by <c>AccessionInput</c>. Manifest: <c>inputs[].accession</c>.
    /// When set, <see cref="InputPath"/> and <see cref="AnnotationPath"/> are null: there is no file to copy or upload.
    /// </summary>
    public string? Accession { get; init; }

    /// <summary>The <c>start-end</c> part of <see cref="Accession"/> to fetch (1-based, inclusive), or null for the whole record. Manifest: <c>inputs[].accessionRange</c>.</summary>
    public string? AccessionRange { get; init; }

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

    // ---- Topology and presets (issue #136) ----

    /// <summary>Whether each contig is treated as a circle (a plasmid). Manifest: <c>analysis.topology</c>, written only when not <see cref="Topology.Auto"/>.</summary>
    public Topology Topology { get; init; } = Topology.Auto;

    /// <summary>The id of the option preset this run started from, or null when the options were set by hand. Recorded with the run; not sent to the worker.</summary>
    public string? PresetId { get; init; }

    /// <summary>The preset's name as the user knew it when the run started (a user preset can be renamed or deleted later). Recorded with the run; not sent to the worker.</summary>
    public string? PresetName { get; init; }

    // ---- Genes ----

    /// <summary>
    /// Find genes (prokaryotic annotation via Prodigal; FASTA/paste input only - a GenBank
    /// input already carries its own genes, Hard Rule "use its genes as-is"). Manifest:
    /// <c>inputs[].genes</c>. Default On.
    /// </summary>
    public bool FindGenes { get; init; } = true;

    // ---- Output ----

    /// <summary>Which output files to write. Manifest: <c>outputs</c> (an empty/omitted array there means "everything", so one is never sent). Defaults to <see cref="OutputCatalog.Defaults" />.</summary>
    public OutputFileKinds OutputFiles { get; init; } = OutputCatalog.Defaults;

    /// <summary>
    /// The primary track's on-disk format when both are possible. Manifest:
    /// <c>analysis.format</c> ("bedgraph" / "wig"). Only meaningful when
    /// <see cref="OutputFileKinds.BedGraph" /> or <see cref="OutputFileKinds.Wig" /> is set
    /// in <see cref="OutputFiles" />.
    /// </summary>
    public TrackFormat TrackFormat { get; init; } = TrackFormat.BedGraph;

    /// <summary>The centred window, in bases, of the GC-percent track (<see cref="OutputFileKinds.Gc"/>). Manifest: <c>analysis.gcWindow</c>, sent only when the GC output is chosen. Odd, 1 to 100001; default 51, the worker's own default (#688).</summary>
    public int GcWindow { get; init; } = 51;

    /// <summary>Region caller options (worker manifest analysis.regionThreshold, regionMinLength, regionMergeGap; issue #552). Sent only when the regions file is chosen. The defaults are the worker's own.</summary>
    public double RegionThreshold { get; init; } = 0.5;

    public int RegionMinLength { get; init; } = 20;

    public int RegionMergeGap { get; init; } = 5;

    /// <summary>Window sizes (odd numbers of bases) of the smoothed entropy tracks (analysis.smoothingWindows; issue #552). Sent only when the smoothed tracks are chosen.</summary>
    public IReadOnlyList<int> SmoothingWindows { get; init; } = [51];

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

    /// <summary>
    /// Issue #92: wait for a graphics card (Compute Engine's flex-start). Off by default; the stockout card on the Runs page offers it, and Advanced options can
    /// set it up front. The run is queued by Google until a card is free instead of failing at once.
    /// </summary>
    public bool WaitForGpu { get; init; }

    /// <summary>Issue #92: how long a queued run waits for a card before it gives up, in minutes (default 2 hours). Only read when <see cref="WaitForGpu"/> is on.</summary>
    public int WaitForGpuMinutes { get; init; } = 120;

    /// <summary>What the worker does to its own VM once every output is uploaded. Manifest: <c>lifecycle.afterTask</c>. Default Stop (Hard Rule 11).</summary>
    public AfterTaskAction AfterTask { get; init; } = AfterTaskAction.Stop;

    /// <summary>Manifest: <c>lifecycle.keepAliveMinutes</c>. Only consulted when <see cref="AfterTask" /> is <see cref="AfterTaskAction.KeepAlive" />. Range 5..240 per spec.</summary>
    public int KeepAliveMinutes { get; init; } = 30;

    /// <summary>What happens once the keep-alive window above expires. Manifest: <c>lifecycle.afterKeepAlive</c>. "Keep alive always has an expiry" (Hard Rule 11) - this enum has no keep-forever value on purpose.</summary>
    public AfterKeepAliveAction AfterKeepAlive { get; init; } = AfterKeepAliveAction.Stop;

    /// <summary>The VM's own safety-cap backstop (Hard Rule 10), independent of the app's own polling. Manifest: <c>limits.maxRunSeconds</c> is derived from this (default 240 min = 4 h = 14,400 s, matching the worker's own default).</summary>
    public int MaxRunDurationMinutes { get; init; } = 240;

    /// <summary>How long cloud-side outputs are kept before the bucket's own lifecycle rule deletes them. Not sent to the worker; consumed by bucket provisioning.</summary>
    public int CloudResultsRetentionDays { get; init; } = Cloud.ResultsBucket.DefaultRetentionDays;

    /// <summary>Boot disk size for the VM. Not sent to the worker manifest; consumed by <c>VmSpec</c>.</summary>
    public int BootDiskGb { get; init; } = 150;

    // ---- Local ----
    // The "when a run finishes" switches (toast, open folder, open results) are NOT run options: they are app settings
    // (Presentation/Services/NotificationSettings, issue #643) and were removed from here as dead duplicates.

    /// <summary>
    /// Copy the worker log into the output folder as well as the app-data copy that is
    /// always kept. Default Off - the log is always kept under
    /// <c>%LOCALAPPDATA%\DNAEntropyGraph\logs\</c> regardless of this flag.
    /// </summary>
    public bool KeepWorkerLogInOutputFolder { get; init; }
}

/// <summary>Whether a contig is a line or a circle. Wire name: <c>analysis.topology</c> ("auto" | "linear" | "circular"); auto lets a GenBank LOCUS line decide.</summary>
public enum Topology
{
    Auto,
    Linear,
    Circular,
}

/// <summary>Spec 4.3 "Input / Format" row. Wire name: <c>inputs[].informat</c> ("auto" | "genbank" | "fasta" | "paste" | "embl" | "snapgene").</summary>
public enum InputFormat
{
    Auto,
    GenBank,
    Fasta,
    Plain,
    Embl,
    SnapGene,
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

    /// <summary>Two H100 80 GB in one VM (<c>a3-highgpu-2g</c>): what evo2_40b needs (issue #87). Appended last so a stored run's tier name keeps its meaning.</summary>
    H100x2,
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
    Smoothed = 1 << 8,
    Regions = 1 << 9,
    GeneSummary = 1 << 10,
    Probs = 1 << 11,
    ProbsNpy = 1 << 12,
    Gc = 1 << 13,
    Codons = 1 << 14,

    /// <summary>
    /// FROZEN at the eight files that existed before issue #103. A stored run says "All" by name (the JSON converter writes a
    /// flags value that equals a named member by that name), so widening this would silently change what an old row meant.
    /// New code uses <see cref="OutputCatalog.Defaults" /> or <see cref="OutputCatalog.Everything" />.
    /// </summary>
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
