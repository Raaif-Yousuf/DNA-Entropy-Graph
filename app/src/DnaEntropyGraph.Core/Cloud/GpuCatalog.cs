namespace DnaEntropyGraph.Core.Cloud;

/// <summary>Why a model does or does not run on a GPU tier (issue #87). Anything but <see cref="Fits"/> is an infeasible pair.</summary>
public enum GpuFit
{
    Fits,

    /// <summary>The model is FP8 and needs a Hopper card (H100); this GPU is not one.</summary>
    NeedsHopper,

    /// <summary>The model needs more graphics cards in one computer than this tier has.</summary>
    NeedsMoreGpus,

    /// <summary>One card here has less memory than the model needs.</summary>
    NeedsMoreMemory,

    /// <summary>The model id is not in the catalog.</summary>
    UnknownModel,

    /// <summary>The model is known, but no GPU the app offers can run it (so an automatic choice has nothing to pick).</summary>
    NoOfferedGpu,
}

/// <summary>
/// One model. Precision, Hopper and card count are guarded against <c>worker/src/dna_entropy/predictors/hardware.py</c> <c>MODEL_REQUIREMENTS</c>
/// by <c>WorkerContractGuardTests</c> (the worker refuses a run whose hardware disagrees, so a wrong row here would cost the user a failed run).
/// </summary>
/// <param name="Id">The wire id (<c>predictor.model</c>).</param>
/// <param name="Precision">"bf16" or "fp8".</param>
/// <param name="SizeBillions">Parameters, in billions: what "bigger model" means when a GPU pick suggests one.</param>
/// <param name="MinGpuCount">Graphics cards in one computer.</param>
/// <param name="MinVramGbPerGpu">Memory one card must have.</param>
/// <param name="NeedsHopper">FP8 through Transformer Engine, which only Hopper cards (H100) run.</param>
/// <param name="Offered">Whether the New run page lets the user pick it. The single switch issue #14 (an owner DECISION) turns for evo2_40b and evo2_1b_base.</param>
public sealed record ModelEntry(string Id, string Precision, int SizeBillions, int MinGpuCount, int MinVramGbPerGpu, bool NeedsHopper, bool Offered);

/// <summary>One GPU tier and the machine that carries it.</summary>
/// <param name="Tier">The tier a run's options name.</param>
/// <param name="MachineType">The Compute Engine machine type (<c>VmSpec.MachineType</c>).</param>
/// <param name="Accelerator">The accelerator type id Compute Engine uses (what a quota check names, issue #414).</param>
/// <param name="QuotaMetric">The regional quota metric for that accelerator (see <see cref="QuotaMetrics"/>).</param>
/// <param name="GpuCount">Graphics cards in the machine.</param>
/// <param name="VramGbPerGpu">Memory of one card.</param>
/// <param name="IsHopper">Whether the card is a Hopper (H100).</param>
/// <param name="SpotOnly">Compute Engine offers this shape on Spot or Flex-start only, so a run on it must be a Spot run (the pickers set <c>RunOptions.SpotVm</c>). THEORY (unverified against a real project): Appendix B section 4.1 says "not offered on-demand for small A3 shapes", which this catalog reads as both a3-highgpu-1g and 2g.</param>
/// <param name="ContextCeiling">The largest window (bases) one forward pass fits on this tier, for the 7B model; null where it has not been measured (science_and_formats.md section 3). Never guessed.</param>
/// <param name="Offered">Whether the page lets the user pick the tier.</param>
public sealed record GpuEntry(
    GpuTier Tier,
    string MachineType,
    string Accelerator,
    string QuotaMetric,
    int GpuCount,
    int VramGbPerGpu,
    bool IsHopper,
    bool SpotOnly,
    int? ContextCeiling,
    bool Offered);

/// <summary>
/// The models and GPU tiers the app knows, and which of them it offers (issue #87). The one place the hardware facts and the issue #14
/// visibility live: <see cref="CloudJobRequestFactory.MachineTypeFor"/>, the New run page's pickers and its cost estimate all read it.
/// </summary>
public sealed class GpuCatalog
{
    private readonly IReadOnlyList<ModelEntry> _models;
    private readonly IReadOnlyList<GpuEntry> _gpus;

    public GpuCatalog(IReadOnlyList<ModelEntry> models, IReadOnlyList<GpuEntry> gpus)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(gpus);
        _models = models;
        _gpus = gpus;
    }

    /// <summary>
    /// What ships. MEASURED 2026-10-15: until now the app offered only evo2_7b on an L4 (no model picker existed), so that is what is offered here and
    /// issue #14 (ship evo2_40b and evo2_1b_base, or hide them) stays undecided: to decide it, flip <c>Offered</c> below (and the one assertion in
    /// GpuCatalogTests). GPUs are listed cheapest first: an automatic choice takes the first one that fits. THEORY (unverified): the A100 and H100
    /// window ceilings are unmeasured (null), prices are in pricing.json, not here.
    /// </summary>
    public static GpuCatalog Shipped { get; } = new(
        [
            new ModelEntry("evo2_7b", "bf16", 7, 1, 24, NeedsHopper: false, Offered: true),
            new ModelEntry("evo2_7b_262k", "bf16", 7, 1, 24, NeedsHopper: false, Offered: false),
            new ModelEntry("evo2_1b_base", "fp8", 1, 1, 80, NeedsHopper: true, Offered: false),
            new ModelEntry("evo2_20b", "fp8", 20, 1, 80, NeedsHopper: true, Offered: false),
            new ModelEntry("evo2_40b", "fp8", 40, 2, 80, NeedsHopper: true, Offered: false),
        ],
        [
            new GpuEntry(GpuTier.L4, "g2-standard-8", "nvidia-l4", QuotaMetrics.NvidiaL4Gpus, 1, 24, IsHopper: false, SpotOnly: false, ContextCeiling: 8192, Offered: true),
            new GpuEntry(GpuTier.A100_40, "a2-highgpu-1g", "nvidia-tesla-a100", QuotaMetrics.NvidiaA100Gpus, 1, 40, IsHopper: false, SpotOnly: false, ContextCeiling: null, Offered: true),
            new GpuEntry(GpuTier.A100_80, "a2-ultragpu-1g", "nvidia-a100-80gb", QuotaMetrics.NvidiaA100Gpus80Gb, 1, 80, IsHopper: false, SpotOnly: false, ContextCeiling: null, Offered: true),
            new GpuEntry(GpuTier.H100, "a3-highgpu-1g", "nvidia-h100-80gb", QuotaMetrics.NvidiaH100Gpus, 1, 80, IsHopper: true, SpotOnly: true, ContextCeiling: null, Offered: false),
            new GpuEntry(GpuTier.H100x2, "a3-highgpu-2g", "nvidia-h100-80gb", QuotaMetrics.NvidiaH100Gpus, 2, 80, IsHopper: true, SpotOnly: true, ContextCeiling: null, Offered: false),
        ]);

    public IReadOnlyList<ModelEntry> Models => _models;

    /// <summary>Every GPU tier, cheapest first.</summary>
    public IReadOnlyList<GpuEntry> Gpus => _gpus;

    public IEnumerable<ModelEntry> OfferedModels => _models.Where(m => m.Offered);

    public IEnumerable<GpuEntry> OfferedGpus => _gpus.Where(g => g.Offered);

    public ModelEntry? FindModel(string modelId)
        => _models.FirstOrDefault(m => string.Equals(m.Id, modelId, StringComparison.OrdinalIgnoreCase));

    /// <summary>The entry for a concrete tier; <see cref="GpuTier.CheapestAvailable"/> is not a tier of its own and throws.</summary>
    public GpuEntry Gpu(GpuTier tier)
        => _gpus.FirstOrDefault(g => g.Tier == tier) ?? throw new ArgumentOutOfRangeException(nameof(tier), tier, "Not a concrete GPU tier in the catalog.");

    /// <summary>A copy that offers every model and tier: what the pickers would show once issue #14 is decided to show them all (for tests).</summary>
    public GpuCatalog WithEverythingOffered()
        => new([.. _models.Select(m => m with { Offered = true })], [.. _gpus.Select(g => g with { Offered = true })]);

    /// <summary>Whether <paramref name="modelId"/> runs on <paramref name="tier"/>, and if not, the first reason it does not (Hopper, then card count, then memory).</summary>
    public GpuFit FitOf(string modelId, GpuTier tier)
    {
        if (FindModel(modelId) is not { } model)
        {
            return GpuFit.UnknownModel;
        }

        var gpu = Gpu(tier);
        if (model.NeedsHopper && !gpu.IsHopper)
        {
            return GpuFit.NeedsHopper;
        }

        if (gpu.GpuCount < model.MinGpuCount)
        {
            return GpuFit.NeedsMoreGpus;
        }

        return gpu.VramGbPerGpu < model.MinVramGbPerGpu ? GpuFit.NeedsMoreMemory : GpuFit.Fits;
    }
}
