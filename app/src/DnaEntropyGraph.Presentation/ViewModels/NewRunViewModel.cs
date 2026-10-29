using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Contract;
using DnaEntropyGraph.Core.Cost;
using DnaEntropyGraph.Core.Inputs;
using DnaEntropyGraph.Presentation.Services;

namespace DnaEntropyGraph.Presentation.ViewModels;

/// <summary>
/// Drop files, folders or pasted text, see each one checked locally, name the run, press Run
/// (issue #63, docs/superpowers/specs Appendix A section 2.2). Every file is validated here with the same
/// rules as the worker before any cloud resource exists (Hard Rule 2); <c>JobEngine</c> validates once more
/// at run start.
/// </summary>
public sealed partial class NewRunViewModel : ObservableObject, INewRunScreen
{
    // The model the page starts with, used where a default RunOptions is wanted (the boot disk and context length defaults). The model a run
    // really uses comes from the linked pickers (Gpu, issue #87), which start on the first model the catalog offers: this one.
    private const string DefaultModelId = "evo2_7b";

    // The tier and the VM kind (on-demand, or Spot where the tier is Spot-only) come from the Gpu pickers and feed both StartRunAsync and the estimate.

    // The boot disk a run on this page gets: the same default StartRunAsync's RunOptions uses, so the disk in the estimate is the disk created.
    private static readonly int DefaultBootDiskGb = new RunOptions { ModelId = DefaultModelId, RunTarget = "Cloud" }.BootDiskGb;

    // A count of this many characters or fewer is worked out on the spot; a bigger paste is counted off the UI thread.
    private const int InlineCountLimit = 20_000;
    private static readonly TimeSpan CountDebounce = TimeSpan.FromMilliseconds(150);

    private readonly IFilePicker _filePicker;
    private readonly IJobEngine _jobEngine;
    private readonly INavigator _navigator;
    private readonly IStringResourceProvider _strings;
    private readonly IPastedInputStore _pastedStore;
    private readonly ICostEstimateService _estimates;
    private readonly IInputFileSystem _files;
    private readonly NewRunIntake? _intake;
    private readonly Func<string, InputFormat, AmbiguityPolicy, bool, InputValidationResult> _validate;
    private readonly OutputSelectionStore _outputStore;
    private readonly IDiagnosticsLog _log;
    private readonly IAccessionLookup? _accessionLookup;
    private readonly IClipboardTextReader? _clipboard;
    private readonly Func<string, string, Topology, AnnotationCheckResult> _validateAnnotation;
    private bool _applyingPreset;

    // The files ticked on the page, per input group (issue #103): read from the remembered choice the first time a group is shown,
    // kept while the user switches between pills, and written back to the store only when a run starts.
    private readonly Dictionary<OutputInputKinds, OutputFileKinds> _outputSelections = [];
    private OutputInputKinds? _shownOutputGroup;

    private InputPillItem? _selectedItem;
    private int _countVersion;
    private int _operationsInFlight;
    private int _estimateVersion;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddPastedCommand))]
    private string _pasteText = string.Empty;

    /// <summary>Whether the Paste sequence section is open; Ctrl+Shift+V opens it (issue #110).</summary>
    [ObservableProperty]
    private bool _isPasteOpen;

    /// <summary>The live "N bases" counter under the Paste sequence box.</summary>
    [ObservableProperty]
    private string _pasteCountText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OfferTreatAsRna))]
    private bool _isTreatingAsRna;

    /// <summary>The line under the file list: what this run will cost and how long it will take, labelled an estimate. Empty when there is nothing to say (no valid file, size not known).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEstimate))]
    private string _estimateText = string.Empty;

    /// <summary>The line after the estimate: what the disk keeps costing per day while the computer sits stopped after the run (the default). Empty whenever <see cref="EstimateText"/> is not an estimate.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEstimateDisk))]
    private string _estimateDiskText = string.Empty;

    /// <summary>A line of help for the user (a path that does not exist, a folder with nothing to run); empty when there is none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusMessage))]
    private string _statusMessage = string.Empty;

    /// <summary>The Cloud / This PC / Auto choice (issues #120 and #173); null in a host that has none, which means Cloud.</summary>
    public RunTargetPickerViewModel? RunTarget { get; }

    public NewRunViewModel(
        IFilePicker filePicker,
        IJobEngine jobEngine,
        INavigator navigator,
        IStringResourceProvider strings,
        IPastedInputStore pastedStore,
        ICostEstimateService estimates,
        OutputSelectionStore outputStore,
        IDiagnosticsLog log,
        GpuPlanViewModel gpu,
        Func<string, InputFormat, AmbiguityPolicy, bool, InputValidationResult>? validate = null,
        IInputFileSystem? files = null,
        NewRunIntake? intake = null,
        IClipboardTextReader? clipboard = null,
        PresetPickerViewModel? presets = null,
        Func<string, string, Topology, AnnotationCheckResult>? validateAnnotation = null,
        bool? offerAnnotation = null,
        bool? offerAccession = null,
        IAccessionLookup? accessionLookup = null,
        RunTargetPickerViewModel? runTarget = null)
    {
        // Required, no fallback: the pickers come from DI, so a missing registration fails resolution instead of quietly showing default pickers (#87).
        Gpu = gpu ?? throw new ArgumentNullException(nameof(gpu));
        Gpu.Changed += OnGpuPlanChanged;
        OffersAccession = offerAccession ?? WorkerManifestSupport.Accession;
        _accessionLookup = accessionLookup;
        RunTarget = runTarget;
        _clipboard = clipboard;
        _validateAnnotation = validateAnnotation ?? ((fasta, gff, topology) => AnnotationPairValidator.Check(fasta, gff, topology));
        OffersAnnotation = offerAnnotation ?? WorkerManifestSupport.Annotation;
        Presets = presets;
        if (presets is not null)
        {
            presets.OutputsChosen += ApplyPresetOutputs;
        }

        _intake = intake;
        _outputStore = outputStore;
        _log = log;
        _filePicker = filePicker;
        _jobEngine = jobEngine;
        _navigator = navigator;
        _strings = strings;
        _pastedStore = pastedStore;
        _estimates = estimates;
        _files = files ?? new LocalInputFileSystem();
        _validate = validate ?? ((path, format, policy, rna) => InputFileValidator.Validate(path, format, policy, rna));
        _pasteCountText = CountText(0);
        Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasSeveralItems));
    }

    /// <summary>The pill Run will run. Never cleared while pills exist: see the setter.</summary>
    public InputPillItem? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (value is null && Items.Count > 0)
            {
                // MEASURED 2026-10-03 in the real app: after Remove, the ListView pushed null back through the
                // TwoWay binding and left the neighbour unselected with Run disabled. A single-selection list
                // with pills has no way to be deselected by the user, so null here is that echo: refuse it and
                // tell the binding to select what we still have.
                OnPropertyChanged();
                return;
            }

            if (SetProperty(ref _selectedItem, value))
            {
                RefreshOutputChoices();
                StartRunCommand.NotifyCanExecuteChanged();
                AddAnnotationCommand.NotifyCanExecuteChanged();
                _ = RefreshEstimateAsync();
            }
        }
    }

    /// <summary>One pill per file or pasted sequence, in the order added.</summary>
    public ObservableCollection<InputPillItem> Items { get; } = [];

    /// <summary>The "Files you get back" boxes for the selected file's kind (a GenBank file or a FASTA/pasted sequence); empty while the kind is not known.</summary>
    public ObservableCollection<OutputChoiceItem> OutputChoices { get; } = [];

    /// <summary>True while the worker the run starts on reads a paired GFF3 (<see cref="WorkerManifestSupport.Annotation"/>): only then does the page offer Add GFF3 and take a dropped GFF3.</summary>
    public bool OffersAnnotation { get; }

    /// <summary>True while the worker the run starts on fetches an NCBI accession (<see cref="WorkerManifestSupport.Accession"/>): only then does the page offer the Fetch from NCBI box.</summary>
    public bool OffersAccession { get; }

    /// <summary>The accession typed in the Fetch from NCBI box (issue #684).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddAccessionCommand))]
    private string _accessionText = string.Empty;

    /// <summary>Where in the record to start (1-based), or empty for the whole record.</summary>
    [ObservableProperty]
    private string _accessionStartText = string.Empty;

    /// <summary>Where in the record to stop (inclusive), or empty for the whole record.</summary>
    [ObservableProperty]
    private string _accessionEndText = string.Empty;

    /// <summary>What is wrong with the typed accession or range, with the one thing to do; empty when there is nothing to say.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAccessionMessage))]
    private string _accessionMessage = string.Empty;

    public bool HasAccessionMessage => AccessionMessage.Length > 0;

    /// <summary>True while NCBI is being asked whether the typed record exists and fits (Hard Rule 2); the page shows a progress line and Add waits.</summary>
    [ObservableProperty]
    private bool _isCheckingAccession;

    // A message is about what was typed when it was made: any edit makes it stale.
    partial void OnAccessionTextChanged(string value) => AccessionMessage = string.Empty;

    partial void OnAccessionStartTextChanged(string value) => AccessionMessage = string.Empty;

    partial void OnAccessionEndTextChanged(string value) => AccessionMessage = string.Empty;

    /// <summary>The linked model and graphics card pickers (issue #87): what a run uses, what it costs, and whether Run waits for a confirmation.</summary>
    public GpuPlanViewModel Gpu { get; }

    private void OnGpuPlanChanged()
    {
        StartRunCommand.NotifyCanExecuteChanged();
        _ = RefreshEstimateAsync();
    }

    /// <summary>The option preset picker (issue #136); null in a host that offers none.</summary>
    public PresetPickerViewModel? Presets { get; }

    public bool HasOutputChoices => OutputChoices.Count > 0;

    /// <summary>True when every box is cleared: the page then says to choose a file, and Run waits.</summary>
    public bool HasNoOutputChosen => HasOutputChoices && !OutputChoices.Any(choice => choice.IsChecked);

    /// <summary>
    /// Shows the boxes for the selected file's kind. The group already shown keeps its boxes (and any unsaved ticks); a
    /// group shown for the first time starts from what was remembered for it, or the catalog defaults.
    /// </summary>
    private void RefreshOutputChoices()
    {
        OutputInputKinds? group = SelectedItem?.Kind is { } kind ? OutputCatalog.GroupFor(kind) : null;
        if (group == _shownOutputGroup)
        {
            return;
        }

        _shownOutputGroup = group;
        OutputChoices.Clear();
        if (group is { } shown)
        {
            if (!_outputSelections.TryGetValue(shown, out var selection))
            {
                _outputSelections[shown] = selection = _outputStore.Load(shown);
            }

            foreach (var entry in OutputCatalog.For(shown))
            {
                OutputChoices.Add(new OutputChoiceItem(entry, _strings.GetString(entry.LabelKey), selection.HasFlag(entry.Kind), OnOutputChoiceChanged));
            }
        }

        NotifyOutputChoicesChanged();
    }

    private void OnOutputChoiceChanged(OutputChoiceItem choice)
    {
        if (_shownOutputGroup is not { } group)
        {
            return;
        }

        if (!_applyingPreset)
        {
            Presets?.NoteOutputsEdited();
        }

        var selection = _outputSelections[group];
        _outputSelections[group] = choice.IsChecked ? selection | choice.Entry.Kind : selection & ~choice.Entry.Kind;

        // The worker writes one track format per run: ticking one of the two track boxes clears the other.
        if (choice.IsChecked && choice.Entry.Kind is OutputFileKinds.BedGraph or OutputFileKinds.Wig)
        {
            var other = choice.Entry.Kind == OutputFileKinds.Wig ? OutputFileKinds.BedGraph : OutputFileKinds.Wig;
            OutputChoices.FirstOrDefault(c => c.Entry.Kind == other)?.IsChecked = false;
        }

        NotifyOutputChoicesChanged();
    }

    /// <summary>A preset chose these files: they become the choice for each kind of input, and the boxes shown now follow.</summary>
    private void ApplyPresetOutputs(OutputFileKinds outputs)
    {
        foreach (var group in new[] { OutputInputKinds.GenBank, OutputInputKinds.Sequence })
        {
            var offered = OutputCatalog.For(group).Aggregate(OutputFileKinds.None, (all, e) => all | e.Kind);
            var chosen = OutputCatalog.Normalize(outputs & offered);
            if (chosen != OutputFileKinds.None)
            {
                _outputSelections[group] = chosen;
            }
        }

        _applyingPreset = true;
        try
        {
            if (_shownOutputGroup is { } shown)
            {
                foreach (var box in OutputChoices)
                {
                    box.IsChecked = _outputSelections[shown].HasFlag(box.Entry.Kind);
                }
            }
        }
        finally
        {
            _applyingPreset = false;
        }

        NotifyOutputChoicesChanged();
    }

    private void NotifyOutputChoicesChanged()
    {
        Presets?.UpdateSummary(_shownOutputGroup is { } shown && _outputSelections.TryGetValue(shown, out var current) ? current : null);
        OnPropertyChanged(nameof(HasOutputChoices));
        OnPropertyChanged(nameof(HasNoOutputChosen));
        StartRunCommand.NotifyCanExecuteChanged();
        AddAnnotationCommand.NotifyCanExecuteChanged();
    }

    public bool HasEstimate => EstimateText.Length > 0;

    public bool HasEstimateDisk => EstimateDiskText.Length > 0;

    public bool HasStatusMessage => StatusMessage.Length > 0;

    /// <summary>True once there is more than one pill: the page then says Run runs only the selected one (#515 will run them together).</summary>
    public bool HasSeveralItems => Items.Count > 1;

    /// <summary>True while some file has a U and the run is not set to treat input as RNA: the page offers the "Treat as RNA" button.</summary>
    public bool OfferTreatAsRna => !IsTreatingAsRna && Items.Any(item => item.NeedsRnaChoice);

    partial void OnPasteTextChanged(string value)
    {
        var version = Interlocked.Increment(ref _countVersion);
        if (value.Length <= InlineCountLimit)
        {
            PasteCountText = CountText(PastedSequenceCounter.Count(value));
            return;
        }

        // A big paste is not scanned on every keystroke on the UI thread: wait for the typing to settle, count on a worker
        // thread, and only the newest paste may show its count.
        _ = CountLargePasteAsync(value, version);
    }

    private async Task CountLargePasteAsync(string text, int version)
    {
        await Task.Delay(CountDebounce);
        if (version != Volatile.Read(ref _countVersion))
        {
            return;
        }

        var count = await Task.Run(() => PastedSequenceCounter.Count(text));
        if (version == Volatile.Read(ref _countVersion))
        {
            PasteCountText = CountText(count);
        }
    }

    private string CountText(long count) => string.Format(CultureInfo.CurrentCulture, _strings.GetString("NewRunPasteCount"), count);

    /// <summary>
    /// Adds dropped or picked paths: a file becomes a pill, a folder contributes the sequence files directly inside it.
    /// The disk questions (is it a folder, what is inside it) are asked off the UI thread. A drop is the only caller
    /// that can be invoked again while an earlier one is still being checked, and its command takes no
    /// <see cref="CancellationToken"/>: MEASURED 2026-10-03, a cancelable async command cancels its running execution
    /// when it is invoked again, which stopped the checks still queued behind the first file of an earlier drop (they
    /// showed "stopped"). A second drop while the first is still being checked must be added next to it.
    /// </summary>
    private async Task AddPathsAsync(IReadOnlyList<string>? paths, CancellationToken cancellationToken)
    {
        BeginOperation();
        var messages = new List<string>();
        try
        {
            // A GFF3 is not a sequence of its own: it is held back until the FASTA files of this drop are on the page, then paired (issue #686).
            var annotations = new List<string>();
            var added = new List<InputPillItem>();
            foreach (var path in paths ?? [])
            {
                if (await Task.Run(() => _files.DirectoryExists(path), CancellationToken.None))
                {
                    var files = await Task.Run(() => _files.SequenceFiles(path), CancellationToken.None);
                    if (files.Count == 0)
                    {
                        messages.Add(Format("NewRunStatusFolderEmpty", path));
                    }

                    foreach (var file in files)
                    {
                        await AddOrHoldAsync(file, annotations, added, cancellationToken);
                    }
                }
                else
                {
                    await AddOrHoldAsync(path, annotations, added, cancellationToken);
                }
            }

            await PairAnnotationsAsync(annotations, added, messages, cancellationToken);
        }
        finally
        {
            EndOperation();
            PostStatus(messages);
        }
    }

    private async Task AddOrHoldAsync(string path, List<string> annotations, List<InputPillItem> added, CancellationToken cancellationToken)
    {
        if (AnnotationFileTypes.IsAnnotationFile(path))
        {
            annotations.Add(path);
            return;
        }

        var (pill, _) = await AddFileAsync(path, isPasted: false, cancellationToken);
        if (pill is not null)
        {
            added.Add(pill);
        }
    }

    /// <summary>
    /// Pairs each dropped GFF3 with a FASTA pill: the one with the same name (without the extension) among the files just added, else the
    /// only FASTA on the page. With several FASTA files and no name that matches, nothing is guessed: the page says to select one and press Add GFF3.
    /// </summary>
    private async Task PairAnnotationsAsync(List<string> annotations, List<InputPillItem> added, List<string> messages, CancellationToken cancellationToken)
    {
        if (annotations.Count == 0)
        {
            return;
        }

        if (!OffersAnnotation)
        {
            messages.Add(_strings.GetString("NewRunStatusAnnotationUnavailable"));
            return;
        }

        // Issue #135, the conservative rules: a GFF3 goes to the FASTA with the same name (case does not matter), whether or not that FASTA
        // passed its check (a same-name FASTA that failed is the name match: the GFF3 is not handed to a different file, the page says why);
        // the only valid FASTA on the page is taken when no FASTA has that name at all. Anything unclear (two FASTA files with that name,
        // wherever they were added, two GFF3 files for one FASTA, a FASTA that already has a GFF3) is left alone and the page says why: the
        // result is always visible on the pill and undone with Remove GFF3. The same GFF3 path twice in one drop is one GFF3.
        var plans = new List<(string Gff, InputPillItem Target, bool ByName)>();
        foreach (var gff in annotations.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var fastas = Items.Where(item => item.Kind == InputKind.Fasta).ToList();
            var stem = Path.GetFileNameWithoutExtension(gff);
            var named = fastas.Where(item => string.Equals(Path.GetFileNameWithoutExtension(item.Path), stem, StringComparison.OrdinalIgnoreCase)).ToList();
            if (named.Count > 1)
            {
                messages.Add(Format("NewRunStatusAnnotationAmbiguousName", Path.GetFileName(gff)));
                continue;
            }

            if (named.Count == 1)
            {
                if (!named[0].IsValid)
                {
                    messages.Add(Format("NewRunStatusAnnotationNamedInvalid", Path.GetFileName(gff), Path.GetFileName(named[0].Path)));
                    continue;
                }

                plans.Add((gff, named[0], true));
                continue;
            }

            var candidates = fastas.Where(item => item.IsValid).ToList();
            if (candidates.Count == 1)
            {
                plans.Add((gff, candidates[0], false));
            }
            else
            {
                messages.Add(_strings.GetString(candidates.Count == 0 ? "NewRunStatusAnnotationNoFasta" : "NewRunStatusAnnotationAmbiguous"));
            }
        }

        foreach (var group in plans.GroupBy(p => p.Target))
        {
            var target = group.Key;
            var byNameInGroup = group.Where(p => p.ByName).ToList();
            if (byNameInGroup.Count > 0)
            {
                // The exact name match wins; a guess made only because there was one FASTA yields to it (and says so).
                foreach (var guess in group.Where(p => !p.ByName))
                {
                    messages.Add(Format("NewRunStatusAnnotationFallbackRefused", Path.GetFileName(guess.Gff), Path.GetFileName(target.Path)));
                }
            }

            var candidatesForTarget = byNameInGroup.Count > 0 ? byNameInGroup : group.ToList();
            if (candidatesForTarget.Count > 1)
            {
                messages.Add(Format("NewRunStatusAnnotationTwoForOne", Path.GetFileName(target.Path)));
                continue;
            }

            var (gff, _, byName) = candidatesForTarget.Single();
            if (target.AnnotationPath is { } existing)
            {
                if (!string.Equals(existing, gff, StringComparison.OrdinalIgnoreCase))
                {
                    messages.Add(Format("NewRunStatusAnnotationAlreadyPaired", Path.GetFileName(target.Path), Path.GetFileName(existing), Path.GetFileName(gff)));
                }

                continue;
            }

            await AttachAnnotationAsync(target, gff, messages, cancellationToken);
            if (!byName && target.AnnotationPath == gff)
            {
                // Pairing by position, not by name, is a guess the user should see (and can undo).
                messages.Add(Format("NewRunStatusAnnotationPairedOnly", Path.GetFileName(gff), Path.GetFileName(target.Path)));
            }
        }
    }
    /// <summary>Checks the GFF3 against the FASTA on this PC (Hard Rule 2) and, when it fits, shows it on the pill. A GFF3 that does not fit is not kept: the page says why.</summary>
    private async Task AttachAnnotationAsync(InputPillItem pill, string gffPath, List<string> messages, CancellationToken cancellationToken)
    {
        var topology = Presets?.Topology ?? Topology.Auto;
        AnnotationCheckResult result;
        try
        {
            result = await Task.Run(() => _validateAnnotation(pill.Path, gffPath, topology), cancellationToken).WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (result.Problem is { } problem)
        {
            messages.Add(_strings.GetString(AnnotationProblemCopy.KeyFor(problem.Code)));
            return;
        }

        pill.ApplyAnnotation(gffPath, result);
    }

    private bool CanAddAnnotation() => OffersAnnotation && SelectedItem is { Kind: InputKind.Fasta, IsValid: true };

    /// <summary>The Add GFF3 button: chooses a GFF3 for the selected FASTA when its name does not say which one it belongs to.</summary>
    [RelayCommand(CanExecute = nameof(CanAddAnnotation))]
    private async Task AddAnnotationAsync(CancellationToken cancellationToken)
    {
        var pill = SelectedItem;
        if (pill is null)
        {
            return;
        }

        string? picked;
        try
        {
            picked = await _filePicker.PickAnnotationFileAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            ReportFailure(ex, "NewRunStatusBrowseFailed");
            return;
        }

        if (string.IsNullOrEmpty(picked))
        {
            return;
        }

        BeginOperation();
        var messages = new List<string>();
        try
        {
            await AttachAnnotationAsync(pill, picked, messages, cancellationToken);
        }
        finally
        {
            EndOperation();
            PostStatus(messages);
        }
    }

    /// <summary>Takes the paired GFF3 off the selected FASTA (the FASTA stays), so the worker finds the genes itself again.</summary>
    [RelayCommand]
    private void RemoveAnnotation(InputPillItem? item) => item?.ClearAnnotation();

    /// <summary>Starts something that may say a line of help. The status line is cleared only by the operation that finds no other one running, so a second drop does not wipe what the first one is about to say.</summary>
    private void BeginOperation()
    {
        if (_operationsInFlight++ == 0)
        {
            StatusMessage = string.Empty;
        }
    }

    private void EndOperation() => _operationsInFlight--;

    /// <summary>Adds lines to the status line, after any the page already shows.</summary>
    private void PostStatus(IEnumerable<string> lines)
    {
        var added = string.Join('\n', lines);
        if (added.Length > 0)
        {
            StatusMessage = StatusMessage.Length == 0 ? added : StatusMessage + "\n" + added;
        }
    }

    /// <summary>
    /// Says a failure's line like any other operation does (after what another operation in flight has already said, replacing a
    /// stale line only when the page is idle), then logs its class, never its message, which can carry a file name. The line comes
    /// first and the log is best effort: a log that throws must not hide the line or escape the command.
    /// </summary>
    private void ReportFailure(Exception exception, string messageKey)
    {
        BeginOperation();
        try
        {
            PostStatus([_strings.GetString(messageKey)]);
        }
        finally
        {
            EndOperation();
        }

        try
        {
            _log.Warning("new-run", null, ErrorClass(exception));
        }
        catch (Exception)
        {
            // The log is advice for the diagnostics zip; the user has already been told what to do.
        }
    }

    /// <summary>The exception type, plus the cloud error kind when it has one. Never the message.</summary>
    private static string ErrorClass(Exception exception)
        => exception is CloudOperationException cloud ? $"{nameof(CloudOperationException)}:{cloud.Kind}" : exception.GetType().Name;

    /// <summary>
    /// The line for a run that could not start, by what failed. What the runner interface can let out here: a file the app cannot
    /// read while keeping its own copy (local), a sign-in or Google Cloud call that failed (cloud), or anything else, such as the
    /// app's own settings or history store, including a plain <see cref="IOException"/> (a full disk under app data is not the user's file).
    /// </summary>
    private static string StartFailureKey(Exception exception) => exception switch
    {
        FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException => "NewRunStatusStartFailedFile",
        CloudOperationException or AccountAuthException => "NewRunStatusStartFailedCloud",
        _ => "NewRunStatusStartFailedOther",
    };

    /// <summary>A drop: adds its real paths, and says one action when some items had no path or the drop could not be read.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task AddDroppedAsync(DroppedItems? dropped)
    {
        if (dropped is null)
        {
            return;
        }

        await AddPathsAsync(dropped.Paths, CancellationToken.None);
        if (dropped.Failed)
        {
            PostStatus([_strings.GetString("NewRunStatusDropFailed")]);
        }
        else if (dropped.SkippedVirtual > 0)
        {
            PostStatus([_strings.GetString("NewRunStatusDropVirtual")]);
        }
    }

    /// <summary>
    /// What the page forwards from its navigation parameter (issue #108): files handed over by "Open with" in Explorer arrive as
    /// <see cref="DroppedItems"/> and are added like a drop; any other parameter (none, the usual case) is ignored.
    /// </summary>
    /// <remarks>It is also the "this screen is now showing" call: the screen attaches to the <see cref="NewRunIntake"/>, so later Open-with files are added here instead of to a fresh page.</remarks>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task ReceiveNavigationParameterAsync(object? parameter)
    {
        _intake?.Attach(this);

        // Issue #527: Back re-delivers the parameter the page was first opened with; only a new navigation's parameter is acted on.
        if (parameter is NavigationArrival arrival)
        {
            parameter = arrival.IsNewNavigation ? arrival.Parameter : null;
        }

        return parameter is DroppedItems dropped ? AddDroppedAsync(dropped) : Task.CompletedTask;
    }

    /// <summary>The page was left: files handed over from now on open a New run page instead of being added to this one.</summary>
    public void DetachFromIntake() => _intake?.Detach(this);

    /// <inheritdoc />
    void INewRunScreen.Add(DroppedItems items) => AddDroppedCommand.Execute(items);

    [RelayCommand]
    private async Task BrowseAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<string> picked;
        try
        {
            picked = await _filePicker.PickInputFilesAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            // The picker is a COM call that can fail for reasons we cannot name (no window yet, a shell extension).
            ReportFailure(ex, "NewRunStatusBrowseFailed");
            return;
        }

        await AddPathsAsync(picked, cancellationToken);
    }

    /// <summary>
    /// Adds what is in the Paste sequence box: the path of an existing file adds that file, anything else is a sequence
    /// saved under app data. The box is cleared only when the paste became a pill the user can use (or fix with Treat
    /// as RNA); a paste with a problem stays in the box so it can be edited, and the problem is said in the status line.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAddPasted))]
    private async Task AddPastedAsync(CancellationToken cancellationToken)
    {
        BeginOperation();
        try
        {
            await AddPastedCoreAsync(cancellationToken);
        }
        finally
        {
            EndOperation();
        }
    }

    private async Task AddPastedCoreAsync(CancellationToken cancellationToken)
    {
        var text = PasteText;
        var resolution = await Task.Run(() => _files.Resolve(text), CancellationToken.None);
        switch (resolution.Kind)
        {
            case InputResolutionKind.ExistingFile:
                await AddFileAsync(resolution.FilePath!, isPasted: false, cancellationToken);
                break;
            case InputResolutionKind.PathNotFound:
                PostStatus([Format("NewRunStatusPathNotFound", resolution.AttemptedPath!)]);
                return;
            case InputResolutionKind.PastedSequence:
                if (!await AddPastedSequenceAsync(text, cancellationToken))
                {
                    return;
                }

                break;
            default:
                return;
        }

        PasteText = string.Empty;
    }

    private async Task<bool> AddPastedSequenceAsync(string text, CancellationToken cancellationToken)
    {
        string path;
        try
        {
            path = await Task.Run(() => _pastedStore.Save(text), cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PostStatus([_strings.GetString("NewRunStatusPasteFailed")]);
            return false;
        }

        var (pill, applied) = await AddFileAsync(path, isPasted: true, cancellationToken);
        if (pill is null)
        {
            // Never became a pill (the add was cancelled first): nothing owns the saved copy, so it goes.
            await Task.Run(() => _pastedStore.Delete(path), CancellationToken.None);
            return false;
        }

        if (!applied || pill.IsValid || pill.NeedsRnaChoice)
        {
            // Not applied: a newer check of this pill replaced ours (or ours was cancelled), so we do not know it is
            // bad and the pill stays in the list with its saved copy. The newer check, or the user's Remove, decides.
            return true;
        }

        // Our own check found nothing the user can use: take the pill and its saved copy away, keep the text, say what is wrong.
        PostStatus([pill.ErrorText]);
        await RemoveItemAsync(pill);
        return false;
    }

    /// <summary>Ctrl+Shift+V (issue #110): puts the clipboard's text in the Paste sequence box and opens it. Does not add it; the user still presses Add.</summary>
    public async Task PasteFromClipboardAsync()
    {
        var text = _clipboard is null ? null : await _clipboard.ReadTextAsync();
        if (string.IsNullOrWhiteSpace(text))
        {
            PostStatus([_strings.GetString("NewRunStatusClipboardEmpty")]);
            return;
        }

        PasteText = text;
        IsPasteOpen = true;
    }

    /// <summary>The void form of <see cref="PasteFromClipboardAsync"/> that the Ctrl+Shift+V KeyboardAccelerator's Invoked event binds to with x:Bind (an event handler cannot return a Task).</summary>
    public async void PasteFromClipboard() => await PasteFromClipboardAsync();

    private bool CanAddPasted() => !string.IsNullOrWhiteSpace(PasteText);

    // Literal keys, so the wiring check (and a reader) can see every string a lookup problem can show.
    private static string LookupProblemKey(AccessionLookupProblem problem) => problem switch
    {
        AccessionLookupProblem.NotFound => "NewRunAccessionLookup_NotFound",
        AccessionLookupProblem.RangeOutsideRecord => "NewRunAccessionLookup_RangeOutsideRecord",
        AccessionLookupProblem.TooLarge => "NewRunAccessionLookup_TooLarge",
        AccessionLookupProblem.RateLimited => "NewRunAccessionLookup_RateLimited",
        AccessionLookupProblem.NcbiTrouble => "NewRunAccessionLookup_NcbiTrouble",
        AccessionLookupProblem.TimedOut => "NewRunAccessionLookup_TimedOut",
        _ => "NewRunAccessionLookup_Unreachable",
    };

    private bool CanAddAccession() => OffersAccession && !string.IsNullOrWhiteSpace(AccessionText);

    /// <summary>
    /// Fetch from NCBI (issue #684): checks the typed accession and range with the worker's own rules (Hard Rule 2), and makes a pill the run
    /// fetches on the VM. A problem stays in the box with one line to fix it; nothing is requested from NCBI on this PC.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAddAccession))]
    private async Task AddAccessionAsync()
    {
        var check = AccessionInput.Check(AccessionText, AccessionStartText, AccessionEndText);
        if (check.Problem is { } problem)
        {
            AccessionMessage = _strings.GetString(problem switch
            {
                AccessionProblem.RangeIncomplete => "NewRunAccessionProblem_RangeIncomplete",
                AccessionProblem.RangeInvalid => "NewRunAccessionProblem_RangeInvalid",
                _ => "NewRunAccessionProblem_NotAnAccession",
            });
            return;
        }

        if (Items.Any(item => item.IsAccession && item.Accession == check.Accession && item.AccessionRange == check.Range))
        {
            AccessionMessage = _strings.GetString("NewRunAccessionAlreadyAdded");
            return;
        }

        // Hard Rule 2: one small request to NCBI from this PC (the same one the run makes again before it starts), so a typo that happens to be a
        // well-formed accession, or a record the worker will not read, is told here and not after a rented computer is billed. Its answer also gives
        // the pill a length, which the cost estimate needs.
        long? length = null;
        if (_accessionLookup is not null)
        {
            AccessionMessage = string.Empty;
            IsCheckingAccession = true;
            try
            {
                var found = await _accessionLookup.LookupAsync(check.Accession, check.Range, CancellationToken.None);
                if (found.Problem is { } lookupProblem)
                {
                    AccessionMessage = _strings.GetString(LookupProblemKey(lookupProblem));
                    return;
                }

                length = found.Length;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _log.Warning("new-run", null, ErrorClass(exception));
                AccessionMessage = _strings.GetString(LookupProblemKey(AccessionLookupProblem.Unreachable));
                return;
            }
            finally
            {
                IsCheckingAccession = false;
            }
        }

        var pill = InputPillItem.ForAccession(check.Accession, check.Range, _strings);
        if (length is { } bases)
        {
            pill.ApplyAccessionLength(bases);
        }

        Items.Add(pill);
        SelectedItem ??= pill;
        AccessionText = string.Empty;
        AccessionStartText = string.Empty;
        AccessionEndText = string.Empty;
        AccessionMessage = string.Empty;
        RefreshOutputChoices();
        StartRunCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanRemoveItem), AllowConcurrentExecutions = true)]
    private async Task RemoveItemAsync(InputPillItem? item)
    {
        if (item is null)
        {
            return;
        }

        var index = Items.IndexOf(item);
        if (index < 0)
        {
            return;
        }

        Items.RemoveAt(index);
        if (ReferenceEquals(SelectedItem, item))
        {
            SelectedItem = Items.Count == 0 ? null : Items[Math.Min(index, Items.Count - 1)];
        }

        OnPropertyChanged(nameof(OfferTreatAsRna));
        await DeleteSavedPasteAsync(item);
    }

    private static bool CanRemoveItem(InputPillItem? item) => item is not null;

    /// <summary>The "Treat as RNA" button: converts U to T for this run and checks every file again.</summary>
    [RelayCommand]
    private Task TreatAsRnaAsync(CancellationToken cancellationToken) => SetTreatAsRnaAsync(true, cancellationToken);

    [RelayCommand]
    private Task StopTreatingAsRnaAsync(CancellationToken cancellationToken) => SetTreatAsRnaAsync(false, cancellationToken);

    [RelayCommand(CanExecute = nameof(CanStartRun))]
    private async Task StartRunAsync(CancellationToken cancellationToken)
    {
        var group = _shownOutputGroup!.Value;
        var outputs = _outputSelections[group];
        var startedItem = SelectedItem!;
        var options = new RunOptions
        {
            ModelId = Gpu.ModelId,
            RunTarget = RunTarget?.SelectedTarget ?? "Cloud",
            GpuTier = Gpu.Tier,
            SpotVm = Gpu.RequiresSpot,
            InputPath = SelectedItem!.IsAccession ? null : SelectedItem.Path,
            AnnotationPath = SelectedItem.AnnotationPath,
            Accession = SelectedItem.Accession,
            AccessionRange = SelectedItem.AccessionRange,
            TreatAsRna = IsTreatingAsRna,
            Direction = Presets?.Direction ?? Direction.BothCombined,
            ContextLength = Presets?.ContextLength ?? new RunOptions { ModelId = DefaultModelId, RunTarget = "Cloud" }.ContextLength,
            Topology = Presets?.Topology ?? Topology.Auto,
            PresetId = Presets?.PresetId,
            PresetName = Presets?.PresetName,
            OutputFiles = outputs,
            // Both track boxes ticked keeps the default (bedGraph); WIG alone means the WIG track.
            TrackFormat = outputs.HasFlag(OutputFileKinds.Wig) && !outputs.HasFlag(OutputFileKinds.BedGraph) ? TrackFormat.Wig : TrackFormat.BedGraph,
        };
        string jobId;
        try
        {
            jobId = await _jobEngine.StartRunAsync(options, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            // An exception out of a button command would end the app; say what to do instead, for what actually failed.
            ReportFailure(ex, StartFailureKey(ex));
            return;
        }

        // The run has started and is paid for: open its page first, so nothing the store does can leave the user on a Run button
        // that starts a second one.
        try
        {
            _navigator.NavigateTo("RunProgress", jobId);
        }
        finally
        {
            // Also when the navigation throws: the run is paid for, so its choice is still remembered.
            RememberOutputs(group, outputs);

            // Issue #527: this ViewModel outlives the page, so a started run leaves a clean page for the next one.
            await ResetForNextRunAsync(startedItem);
        }
    }

    /// <summary>
    /// Empties the page once a run has started (issue #527): the files, the paste box, the status line and the RNA choice. The preset,
    /// the ticked outputs and the run target stay: they are the user's intent for runs in general (docs/ui_conventions.md section 9). Pasted copies of the files
    /// that were not run are deleted like a Remove would; the copy behind the run that started stays, because a retry restages from
    /// its recorded path. The user's own files are never touched (Hard Rule 14).
    /// </summary>
    private async Task ResetForNextRunAsync(InputPillItem startedItem)
    {
        var dropped = Items.ToList();
        Items.Clear();
        SelectedItem = null;
        PasteText = string.Empty;
        IsPasteOpen = false;
        StatusMessage = string.Empty;
        IsTreatingAsRna = false;
        OnPropertyChanged(nameof(OfferTreatAsRna));
        foreach (var item in dropped.Where(item => !ReferenceEquals(item, startedItem)))
        {
            await DeleteSavedPasteAsync(item);
        }
    }

    private void RememberOutputs(OutputInputKinds group, OutputFileKinds outputs)
    {
        try
        {
            _outputStore.Save(group, outputs);
        }
        catch (Exception)
        {
            // A choice that could not be remembered only means the next run starts from the defaults; it must never reach the command
            // or replace a navigation failure that is already unwinding.
        }
    }

    private bool CanStartRun() => SelectedItem is { IsValid: true } && _shownOutputGroup is not null && !HasNoOutputChosen && Gpu.IsRunnable;

    private async Task SetTreatAsRnaAsync(bool value, CancellationToken cancellationToken)
    {
        IsTreatingAsRna = value;
        foreach (var item in Items.Where(i => !i.IsAccession).ToList())
        {
            if (cancellationToken.IsCancellationRequested)
            {
                // A newer Treat as RNA click (or the user) took over: it checks every file itself.
                return;
            }

            await ValidateAsync(item, cancellationToken);
        }
    }

    /// <summary>The new pill (null when the file is already on the page) and whether this call's own check was the one that reported.</summary>
    private async Task<(InputPillItem? Pill, bool Applied)> AddFileAsync(string path, bool isPasted, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested
            || Items.Any(existing => string.Equals(existing.Path, path, StringComparison.OrdinalIgnoreCase)))
        {
            return (null, false);
        }

        var pill = new InputPillItem(path, isPasted, _strings);
        Items.Add(pill);
        SelectedItem ??= pill;
        var outcome = await ValidateAsync(pill, cancellationToken);
        if (outcome == CheckOutcome.NotStarted)
        {
            // Cancelled between adding the pill and starting its check: no check will ever report, so end the
            // "Checking" state with the one action the user has (Remove, then add it again). Any other outcome needs nothing here:
            // a cancelled check ended its own state, and a superseded one left the pill to the newer check (a Treat as RNA click
            // that is still running must not be marked stopped).
            pill.AbandonIfNeverChecked();
        }

        return (pill, outcome == CheckOutcome.Applied);
    }

    /// <summary>A pasted sequence is our own copy under app data, so removing its pill removes it. A user's file is never touched (Hard Rule 14).</summary>
    private async Task DeleteSavedPasteAsync(InputPillItem item)
    {
        if (item.IsPasted)
        {
            await Task.Run(() => _pastedStore.Delete(item.Path), CancellationToken.None);
        }
    }

    /// <summary>What became of one <see cref="ValidateAsync"/> call.</summary>
    private enum CheckOutcome
    {
        /// <summary>This check's result is the one the pill shows.</summary>
        Applied,

        /// <summary>A newer check of the pill started first, so this result was dropped; the newer check owns the pill.</summary>
        Superseded,

        /// <summary>Cancelled while running: it ended its own checking state (unless a newer check had taken over).</summary>
        Cancelled,

        /// <summary>Cancelled before it began: it touched nothing.</summary>
        NotStarted,
    }

    /// <summary>
    /// Checks one pill. A check whose token was cancelled before it began starts nothing (it would only take a newer
    /// generation than the check that replaced it and then abandon it), and a cancelled or superseded check writes nothing
    /// the pill's newest check does not already own.
    /// </summary>
    private async Task<CheckOutcome> ValidateAsync(InputPillItem pill, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return CheckOutcome.NotStarted;
        }

        var generation = pill.BeginChecking();
        if (ReferenceEquals(pill, SelectedItem))
        {
            // The old estimate was for the old result; a Checking pill must not show it (and an estimate still in flight must not land).
            ClearEstimate();
        }

        var treatAsRna = IsTreatingAsRna;
        var outcome = CheckOutcome.Superseded;
        try
        {
            // Task.Run honours a token only before the delegate starts; WaitAsync is what lets a cancel end the wait for a check
            // already reading the file (the abandoned read finishes on its own and its result is dropped).
            var result = await Task.Run(() => _validate(pill.Path, InputFormat.Auto, AmbiguityPolicy.Keep, treatAsRna), cancellationToken)
                .WaitAsync(cancellationToken);
            outcome = pill.Apply(result, generation) ? CheckOutcome.Applied : CheckOutcome.Superseded;
        }
        catch (OperationCanceledException)
        {
            pill.Abandon(generation);
            outcome = CheckOutcome.Cancelled;
        }

        OnPropertyChanged(nameof(OfferTreatAsRna));
        if (ReferenceEquals(pill, SelectedItem))
        {
            RefreshOutputChoices();
        }

        StartRunCommand.NotifyCanExecuteChanged();

        AddAnnotationCommand.NotifyCanExecuteChanged();
        if (ReferenceEquals(pill, SelectedItem))
        {
            _ = RefreshEstimateAsync();
        }

        return outcome;
    }

    /// <summary>
    /// Works out the estimate line for the selected file. Not awaited by its callers (it must not hold up the checks or the
    /// list), so it never throws: the estimate is advice, and a failure leaves the page without one. Only the newest call may
    /// write the line.
    /// </summary>
    private async Task RefreshEstimateAsync()
    {
        var version = Interlocked.Increment(ref _estimateVersion);
        if (SelectedItem is not { IsValid: true, TotalBases: { } bases })
        {
            EstimateText = string.Empty;
            EstimateDiskText = string.Empty;
            return;
        }

        string text;
        string diskText;
        try
        {
            var result = await _estimates.EstimateAsync(CloudJobRequestFactory.MachineTypeFor(Gpu.Tier), Gpu.RequiresSpot, bases, DefaultBootDiskGb, CancellationToken.None);
            text = EstimateLine(result);
            diskText = result.Estimate is { } estimate
                ? Format("NewRunEstimateStoppedDisk", Format("NewRunEstimateMoney", Money(estimate.StoppedDiskUsdPerDay)))
                : string.Empty;
        }
        catch (Exception)
        {
            text = string.Empty;
            diskText = string.Empty;
        }

        if (version == Volatile.Read(ref _estimateVersion))
        {
            EstimateText = text;
            EstimateDiskText = diskText;
        }
    }

    private void ClearEstimate()
    {
        Interlocked.Increment(ref _estimateVersion);
        EstimateText = string.Empty;
        EstimateDiskText = string.Empty;
    }

    private string EstimateLine(CostEstimateResult result)
    {
        if (result.Estimate is not { } estimate)
        {
            return result.Reason switch
            {
                EstimateUnavailable.UnknownSize or null => string.Empty,
                EstimateUnavailable.NoPriceForMachine => _strings.GetString("NewRunEstimateNoPrice"),
                EstimateUnavailable.NoSpotPrice => _strings.GetString("NewRunEstimateNoSpotPrice"),
                _ => _strings.GetString("NewRunEstimateUnavailable"),
            };
        }

        var lowUsd = Money(estimate.MinUsd);
        var lowMinutes = WholeMinutes(estimate.MinMinutes);
        if (estimate.IsPoint || (lowUsd == Money(estimate.MaxUsd) && lowMinutes == WholeMinutes(estimate.MaxMinutes)))
        {
            // Only a history estimate may say it is based on earlier runs; a model range that rounds to one figure has none.
            return Format(
                estimate.Basis == EstimateBasis.History ? "NewRunEstimateHistory" : "NewRunEstimateModelPoint",
                Format("NewRunEstimateMoney", lowUsd),
                MinutesPhrase(lowMinutes));
        }

        return Format(
            "NewRunEstimateRange",
            Format("NewRunEstimateMoney", lowUsd),
            Format("NewRunEstimateMoney", Money(estimate.MaxUsd)),
            lowMinutes,
            WholeMinutes(estimate.MaxMinutes));
    }

    /// <summary>Dollars to the cent, never shown as zero: a run is never free.</summary>
    private static string Money(double usd) => Math.Max(0.01, Math.Round(usd, 2, MidpointRounding.AwayFromZero)).ToString("0.00", CultureInfo.InvariantCulture);

    private static int WholeMinutes(double minutes) => Math.Max(1, (int)Math.Round(minutes, MidpointRounding.AwayFromZero));

    private string MinutesPhrase(int minutes) => minutes == 1 ? _strings.GetString("NewRunEstimateMinutesOne") : Format("NewRunEstimateMinutesMany", minutes);

    private string Format(string key, params object[] args)
        => string.Format(CultureInfo.CurrentCulture, _strings.GetString(key), args);
}
