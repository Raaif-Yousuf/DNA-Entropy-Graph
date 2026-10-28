namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// What the user has chosen on the linked pickers. <paramref name="Gpu"/> is <see cref="GpuTier.CheapestAvailable"/> for "automatic":
/// the cheapest offered tier that runs the model. A pin keeps the user's choice where it is when the other picker changes.
/// </summary>
public sealed record PlanState(string ModelId, GpuTier Gpu, bool ModelPinned, bool GpuPinned);

/// <summary>What the linker moved on its own, so the page can say so.</summary>
public enum PlanNotice
{
    None,

    /// <summary>The graphics card now in use changed because the model did.</summary>
    GpuSwitched,

    /// <summary>The model changed because the graphics card did.</summary>
    ModelSwitched,
}

/// <param name="State">The state after the change.</param>
/// <param name="ResolvedGpu">The concrete tier a run would start on, or null when automatic has no offered tier that fits.</param>
/// <param name="Fit">Whether that pair runs; anything but <see cref="GpuFit.Fits"/> needs the user to confirm.</param>
/// <param name="Notice">What moved by itself.</param>
public sealed record PlanResult(PlanState State, GpuTier? ResolvedGpu, GpuFit Fit, PlanNotice Notice);

/// <summary>One offered tier and whether the chosen model runs on it: a locked tier is one that does not.</summary>
public sealed record GpuFitRow(GpuTier Tier, GpuFit Fit);

/// <summary>
/// Issue #87, pure: the model picker and the GPU picker are linked, and either can be pinned.
/// Picking a model raises an unpinned GPU to the cheapest offered tier that runs it; picking a GPU moves an unpinned model to the largest
/// offered one that fits. A pinned side never moves, so a pick on the other side can make an infeasible pair, which is reported as a
/// <see cref="GpuFit"/> for the page to warn about and ask the user to confirm.
/// </summary>
public sealed class ModelGpuLinker(GpuCatalog catalog)
{
    public GpuCatalog Catalog { get; } = catalog;

    /// <summary>The initial state: the first offered model and an automatic GPU.</summary>
    public PlanResult Initial()
    {
        var state = new PlanState(Catalog.OfferedModels.First().Id, GpuTier.CheapestAvailable, ModelPinned: false, GpuPinned: false);
        return Describe(state, PlanNotice.None);
    }

    public PlanResult ChooseModel(PlanState state, string modelId)
    {
        ArgumentNullException.ThrowIfNull(state);
        var before = Resolve(state);
        var next = state with { ModelId = modelId };
        if (next.Gpu != GpuTier.CheapestAvailable && !next.GpuPinned && Catalog.FitOf(modelId, next.Gpu) != GpuFit.Fits && Cheapest(modelId) is { } raised)
        {
            next = next with { Gpu = raised };
        }

        var after = Resolve(next);
        return Describe(next, after is not null && after != before ? PlanNotice.GpuSwitched : PlanNotice.None);
    }

    public PlanResult ChooseGpu(PlanState state, GpuTier tier)
    {
        ArgumentNullException.ThrowIfNull(state);
        var next = state with { Gpu = tier };
        var notice = PlanNotice.None;
        if (tier != GpuTier.CheapestAvailable && !next.ModelPinned && SuggestedModel(next.ModelId, tier) is { } suggested && !string.Equals(suggested, next.ModelId, StringComparison.OrdinalIgnoreCase))
        {
            next = next with { ModelId = suggested };
            notice = PlanNotice.ModelSwitched;
        }

        return Describe(next, notice);
    }

    public PlanResult SetModelPinned(PlanState state, bool pinned) => Describe(state with { ModelPinned = pinned }, PlanNotice.None);

    public PlanResult SetGpuPinned(PlanState state, bool pinned) => Describe(state with { GpuPinned = pinned }, PlanNotice.None);

    /// <summary>The tier a run would start on: the chosen one, or for automatic the cheapest offered tier that runs the model (null when none does).</summary>
    public GpuTier? Resolve(PlanState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.Gpu == GpuTier.CheapestAvailable ? Cheapest(state.ModelId) : state.Gpu;
    }

    /// <summary>Every offered tier with whether the model runs on it, cheapest first: the picker marks the ones that do not as locked.</summary>
    public IReadOnlyList<GpuFitRow> GpuFits(string modelId)
        => [.. Catalog.OfferedGpus.Select(g => new GpuFitRow(g.Tier, Catalog.FitOf(modelId, g.Tier)))];

    private PlanResult Describe(PlanState state, PlanNotice notice)
    {
        var resolved = Resolve(state);
        var fit = resolved is { } tier ? Catalog.FitOf(state.ModelId, tier) : (Catalog.FindModel(state.ModelId) is null ? GpuFit.UnknownModel : GpuFit.NoOfferedGpu);
        return new PlanResult(state, resolved, fit, notice);
    }

    private GpuTier? Cheapest(string modelId)
        => Catalog.OfferedGpus.FirstOrDefault(g => Catalog.FitOf(modelId, g.Tier) == GpuFit.Fits)?.Tier;

    /// <summary>
    /// The model a GPU pick should land on: the largest offered model that runs on it, but only when it is larger than the current one or the
    /// current one does not run there (a tie keeps the current model). Null when no offered model fits, which leaves the pair for the user to confirm.
    /// </summary>
    private string? SuggestedModel(string current, GpuTier tier)
    {
        var fitting = Catalog.OfferedModels.Where(m => Catalog.FitOf(m.Id, tier) == GpuFit.Fits).ToList();
        if (fitting.Count == 0)
        {
            return null;
        }

        var largest = fitting.Max(m => m.SizeBillions);
        var currentEntry = Catalog.FindModel(current);
        var currentFits = currentEntry is not null && fitting.Any(m => m.Id == currentEntry.Id);
        if (currentFits && currentEntry!.SizeBillions >= largest)
        {
            return current;
        }

        return fitting.First(m => m.SizeBillions == largest).Id;
    }
}
