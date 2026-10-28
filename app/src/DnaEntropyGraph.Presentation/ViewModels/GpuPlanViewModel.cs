using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Presentation.Services;

namespace DnaEntropyGraph.Presentation.ViewModels;

/// <summary>One entry in the model picker.</summary>
public sealed class ModelChoiceItem(string id, string label)
{
    public string Id { get; } = id;

    public string Label { get; } = label;
}

/// <summary>One entry in the graphics card picker. A locked card cannot run the chosen model: it can still be picked, which either moves an unpinned model or warns.</summary>
public sealed partial class GpuChoiceItem(GpuTier tier) : ObservableObject
{
    public GpuTier Tier { get; } = tier;

    [ObservableProperty]
    private string _label = string.Empty;

    [ObservableProperty]
    private bool _isLocked;
}

/// <summary>
/// The New run page's linked model and graphics card pickers (issue #87), a thin shell over the pure <see cref="ModelGpuLinker"/>: it turns
/// each pick into a linker call, shows what the linker moved, labels the cards that cannot run the model, and holds the "run anyway" confirmation
/// an infeasible pair needs. <see cref="ModelId"/> and <see cref="Tier"/> are what the run and its cost estimate use.
/// </summary>
public sealed partial class GpuPlanViewModel : ObservableObject
{
    private readonly ModelGpuLinker _linker;
    private readonly IStringResourceProvider _strings;
    private PlanState _state;
    private GpuFit _fit;
    private GpuTier? _resolved;
    private ModelChoiceItem _selectedModel;
    private GpuChoiceItem _selectedGpu;
    private bool _applying;

    public GpuPlanViewModel(GpuCatalog catalog, IStringResourceProvider strings)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _strings = strings;
        _linker = new ModelGpuLinker(catalog);
        foreach (var model in catalog.OfferedModels)
        {
            var key = ModelKey(model.Id);
            var text = strings.GetString(key);
            Models.Add(new ModelChoiceItem(model.Id, text == key ? model.Id : text));
        }

        Gpus.Add(new GpuChoiceItem(GpuTier.CheapestAvailable));
        foreach (var gpu in catalog.OfferedGpus)
        {
            Gpus.Add(new GpuChoiceItem(gpu.Tier));
        }

        var initial = _linker.Initial();
        _state = initial.State;
        _fit = initial.Fit;
        _resolved = initial.ResolvedGpu;
        _selectedModel = Models.First(m => m.Id == _state.ModelId);
        _selectedGpu = Gpus.First(g => g.Tier == _state.Gpu);
        Refresh();
    }

    /// <summary>Raised after every change that can alter the run: the page reprices and re-checks whether Run is allowed.</summary>
    public event Action? Changed;

    public ObservableCollection<ModelChoiceItem> Models { get; } = [];

    public ObservableCollection<GpuChoiceItem> Gpus { get; } = [];

    /// <summary>The model picker only shows when there is a choice to make.</summary>
    public bool HasModelChoice => Models.Count > 1;

    public ModelChoiceItem SelectedModel
    {
        get => _selectedModel;
        set
        {
            if (value is null || ReferenceEquals(value, _selectedModel))
            {
                // A ComboBox echoes null when its items change; there is nothing to choose.
                OnPropertyChanged();
                return;
            }

            Apply(_linker.ChooseModel(_state, value.Id));
        }
    }

    public GpuChoiceItem SelectedGpu
    {
        get => _selectedGpu;
        set
        {
            if (value is null || ReferenceEquals(value, _selectedGpu))
            {
                OnPropertyChanged();
                return;
            }

            Apply(_linker.ChooseGpu(_state, value.Tier));
        }
    }

    public bool ModelPinned
    {
        get => _state.ModelPinned;
        set
        {
            if (value != _state.ModelPinned)
            {
                Apply(_linker.SetModelPinned(_state, value));
            }
        }
    }

    public bool GpuPinned
    {
        get => _state.GpuPinned;
        set
        {
            if (value != _state.GpuPinned)
            {
                Apply(_linker.SetGpuPinned(_state, value));
            }
        }
    }

    /// <summary>What the linker moved on its own ("Switched the graphics card to ... for ..."); empty when nothing moved.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    private string _noticeText = string.Empty;

    /// <summary>Why the pair will not run, with what to do about it; empty when it fits.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarning))]
    private string _warningText = string.Empty;

    /// <summary>The user's "run anyway" tick for an infeasible pair. Any change to the pair withdraws it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunnable))]
    private bool _isConfirmed;

    public bool HasNotice => NoticeText.Length > 0;

    public bool HasWarning => WarningText.Length > 0;

    /// <summary>True when the pair fits or the user confirmed it will not: Run waits on this.</summary>
    public bool IsRunnable => _fit == GpuFit.Fits || (IsConfirmed && CanConfirm);

    /// <summary>
    /// True when a warning can be overridden with "Run anyway": there is a card to run on. When no offered card can run the model at all there is
    /// nothing to confirm (the warning says to choose another model), so the tick is not shown and Run stays off.
    /// </summary>
    public bool CanConfirm => _fit != GpuFit.Fits && _resolved is not null;

    /// <summary>True when the chosen tier is available as Spot or Flex-start only, so the run must be a Spot run (<c>RunOptions.SpotVm</c>) and is priced as one.</summary>
    public bool RequiresSpot => _resolved is { } tier && _linker.Catalog.Gpu(tier).SpotOnly;

    /// <summary>The model the run uses.</summary>
    public string ModelId => _state.ModelId;

    /// <summary>
    /// The tier the run starts on. An automatic choice stays "cheapest available" while the cheapest tier is the answer (the zone ladder of issue #86
    /// owns what that means), and names the tier otherwise, so a model that needs a bigger card never starts on the L4.
    /// </summary>
    public GpuTier Tier => _resolved is { } resolved && !(_state.Gpu == GpuTier.CheapestAvailable && resolved == _linker.Catalog.OfferedGpus.First().Tier)
        ? resolved
        : GpuTier.CheapestAvailable;

    partial void OnIsConfirmedChanged(bool value)
    {
        if (!_applying)
        {
            Changed?.Invoke();
        }
    }

    private void Apply(PlanResult result)
    {
        // A lock toggle changes no pair: the notice of the last automatic move and a "Run anyway" tick stay. Only a changed model or card
        // (what a run would use) clears them.
        var pairChanged = !string.Equals(result.State.ModelId, _state.ModelId, StringComparison.Ordinal) || result.ResolvedGpu != _resolved;
        _state = result.State;
        _fit = result.Fit;
        _resolved = result.ResolvedGpu;
        _selectedModel = Models.First(m => m.Id == _state.ModelId);
        _selectedGpu = Gpus.First(g => g.Tier == _state.Gpu);
        if (pairChanged)
        {
            NoticeText = NoticeFor(result);
            _applying = true;
            try
            {
                // This change raises Changed once, below, not once more from the confirmation's own handler.
                IsConfirmed = false;
            }
            finally
            {
                _applying = false;
            }
        }

        Refresh();
        OnPropertyChanged(nameof(SelectedModel));
        OnPropertyChanged(nameof(SelectedGpu));
        OnPropertyChanged(nameof(ModelPinned));
        OnPropertyChanged(nameof(GpuPinned));
        OnPropertyChanged(nameof(ModelId));
        OnPropertyChanged(nameof(Tier));
        OnPropertyChanged(nameof(IsRunnable));
        OnPropertyChanged(nameof(CanConfirm));
        OnPropertyChanged(nameof(RequiresSpot));
        Changed?.Invoke();
    }

    /// <summary>Relabels the cards for the chosen model (a card that cannot run it says why) and rewrites the warning.</summary>
    private void Refresh()
    {
        var fits = _linker.GpuFits(_state.ModelId).ToDictionary(r => r.Tier, r => r.Fit);
        foreach (var item in Gpus)
        {
            if (item.Tier == GpuTier.CheapestAvailable)
            {
                item.IsLocked = false;
                item.Label = _resolved is { } tier
                    ? Format("NewRunGpuAuto", GpuLabel(tier))
                    : GpuLabel(GpuTier.CheapestAvailable);
                continue;
            }

            var fit = fits.GetValueOrDefault(item.Tier, GpuFit.Fits);
            item.IsLocked = fit != GpuFit.Fits;
            item.Label = item.IsLocked ? Format("NewRunGpuLocked", GpuLabel(item.Tier), _strings.GetString(LockKey(fit))) : GpuLabel(item.Tier);
        }

        WarningText = _fit == GpuFit.Fits
            ? string.Empty
            : _resolved is { } shown
                ? Format("NewRunGpuMisfit", _selectedModel.Label, GpuLabel(shown), _strings.GetString(LockKey(_fit)))
                : Format("NewRunGpuNoCard", _selectedModel.Label);
    }

    private string NoticeFor(PlanResult result) => result.Notice switch
    {
        PlanNotice.GpuSwitched when result.ResolvedGpu is { } tier => Format("NewRunGpuSwitchedNotice", GpuLabel(tier), _selectedModel.Label),
        PlanNotice.ModelSwitched => Format("NewRunModelSwitchedNotice", _selectedModel.Label, GpuLabel(result.ResolvedGpu ?? result.State.Gpu)),
        _ => string.Empty,
    };

    private string Format(string key, params object[] args) => string.Format(CultureInfo.CurrentCulture, _strings.GetString(key), args);

    // Literal keys, so the wiring check (and a reader) can see every string the pickers can show.
    private string GpuLabel(GpuTier tier) => _strings.GetString(tier switch
    {
        GpuTier.L4 => "NewRunGpu_L4",
        GpuTier.A100_40 => "NewRunGpu_A100_40",
        GpuTier.A100_80 => "NewRunGpu_A100_80",
        GpuTier.H100 => "NewRunGpu_H100",
        GpuTier.H100x2 => "NewRunGpu_H100x2",
        _ => "NewRunGpu_Auto",
    });

    private static string ModelKey(string id) => id switch
    {
        "evo2_7b" => "NewRunModel_evo2_7b",
        "evo2_7b_262k" => "NewRunModel_evo2_7b_262k",
        "evo2_1b_base" => "NewRunModel_evo2_1b_base",
        "evo2_20b" => "NewRunModel_evo2_20b",
        "evo2_40b" => "NewRunModel_evo2_40b",
        _ => "NewRunModel_Unknown",
    };

    private static string LockKey(GpuFit fit) => fit switch
    {
        GpuFit.NeedsHopper => "NewRunGpuLock_NeedsHopper",
        GpuFit.NeedsMoreGpus => "NewRunGpuLock_NeedsMoreGpus",
        GpuFit.NeedsMoreMemory => "NewRunGpuLock_NeedsMoreMemory",
        _ => "NewRunGpuLock_NoOfferedGpu",
    };
}
