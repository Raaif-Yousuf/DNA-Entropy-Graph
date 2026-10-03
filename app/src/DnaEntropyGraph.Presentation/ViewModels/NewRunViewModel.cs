using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Inputs;
using DnaEntropyGraph.Presentation.Services;

namespace DnaEntropyGraph.Presentation.ViewModels;

/// <summary>
/// Drop files, folders or pasted text, see each one checked locally, name the run, press Run
/// (issue #63, docs/superpowers/specs Appendix A section 2.2). Every file is validated here with the same
/// rules as the worker before any cloud resource exists (Hard Rule 2); <c>JobEngine</c> validates once more
/// at run start.
/// </summary>
public sealed partial class NewRunViewModel : ObservableObject
{
    // The model the New run page runs. There is no model picker on the page yet (a bigger model needs a confirmation
    // and a cost estimate first), so this is the one model the app offers, not a property nothing binds.
    private const string DefaultModelId = "evo2_7b";

    // A count of this many characters or fewer is worked out on the spot; a bigger paste is counted off the UI thread.
    private const int InlineCountLimit = 20_000;
    private static readonly TimeSpan CountDebounce = TimeSpan.FromMilliseconds(150);

    private readonly IFilePicker _filePicker;
    private readonly IJobEngine _jobEngine;
    private readonly INavigator _navigator;
    private readonly IStringResourceProvider _strings;
    private readonly IPastedInputStore _pastedStore;
    private readonly IInputFileSystem _files;
    private readonly Func<string, InputFormat, AmbiguityPolicy, bool, InputValidationResult> _validate;

    private InputPillItem? _selectedItem;
    private int _countVersion;
    private int _operationsInFlight;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddPastedCommand))]
    private string _pasteText = string.Empty;

    /// <summary>The live "N bases" counter under the Paste sequence box.</summary>
    [ObservableProperty]
    private string _pasteCountText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OfferTreatAsRna))]
    private bool _isTreatingAsRna;

    /// <summary>A line of help for the user (a path that does not exist, a folder with nothing to run); empty when there is none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusMessage))]
    private string _statusMessage = string.Empty;

    public NewRunViewModel(
        IFilePicker filePicker,
        IJobEngine jobEngine,
        INavigator navigator,
        IStringResourceProvider strings,
        IPastedInputStore pastedStore,
        Func<string, InputFormat, AmbiguityPolicy, bool, InputValidationResult>? validate = null,
        IInputFileSystem? files = null)
    {
        _filePicker = filePicker;
        _jobEngine = jobEngine;
        _navigator = navigator;
        _strings = strings;
        _pastedStore = pastedStore;
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
                StartRunCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>One pill per file or pasted sequence, in the order added.</summary>
    public ObservableCollection<InputPillItem> Items { get; } = [];

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
                        await AddFileAsync(file, isPasted: false, cancellationToken);
                    }
                }
                else
                {
                    await AddFileAsync(path, isPasted: false, cancellationToken);
                }
            }
        }
        finally
        {
            EndOperation();
            PostStatus(messages);
        }
    }

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
        catch (Exception)
        {
            // The picker is a COM call that can fail for reasons we cannot name (no window yet, a shell extension).
            StatusMessage = _strings.GetString("NewRunStatusBrowseFailed");
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

    private bool CanAddPasted() => !string.IsNullOrWhiteSpace(PasteText);

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
        var options = new RunOptions
        {
            ModelId = DefaultModelId,
            RunTarget = "Cloud",
            InputPath = SelectedItem!.Path,
            TreatAsRna = IsTreatingAsRna,
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
        catch (Exception)
        {
            // An exception out of a button command would end the app; say what to do instead.
            StatusMessage = _strings.GetString("NewRunStatusStartFailed");
            return;
        }

        _navigator.NavigateTo("RunProgress", jobId);
    }

    private bool CanStartRun() => SelectedItem is { IsValid: true };

    private async Task SetTreatAsRnaAsync(bool value, CancellationToken cancellationToken)
    {
        IsTreatingAsRna = value;
        foreach (var item in Items.ToList())
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
        var applied = await ValidateAsync(pill, cancellationToken);
        if (!applied && pill.IsChecking && cancellationToken.IsCancellationRequested)
        {
            // Cancelled between adding the pill and starting its check: no check will ever report, so end the
            // "Checking" state with the one action the user has (Remove, then add it again).
            pill.AbandonLatest();
        }

        return (pill, applied);
    }

    /// <summary>A pasted sequence is our own copy under app data, so removing its pill removes it. A user's file is never touched (Hard Rule 14).</summary>
    private async Task DeleteSavedPasteAsync(InputPillItem item)
    {
        if (item.IsPasted)
        {
            await Task.Run(() => _pastedStore.Delete(item.Path), CancellationToken.None);
        }
    }

    /// <summary>
    /// Checks one pill. True when this check's result is the one the pill shows. A check whose token was cancelled before
    /// it began starts nothing (it would only take a newer generation than the check that replaced it and then abandon
    /// it), and a cancelled or superseded check writes nothing the pill's newest check does not already own.
    /// </summary>
    private async Task<bool> ValidateAsync(InputPillItem pill, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        var generation = pill.BeginChecking();
        var treatAsRna = IsTreatingAsRna;
        var applied = false;
        try
        {
            // Task.Run honours a token only before the delegate starts; WaitAsync is what lets a cancel end the wait for a check
            // already reading the file (the abandoned read finishes on its own and its result is dropped).
            var result = await Task.Run(() => _validate(pill.Path, InputFormat.Auto, AmbiguityPolicy.Keep, treatAsRna), cancellationToken)
                .WaitAsync(cancellationToken);
            applied = pill.Apply(result, generation);
        }
        catch (OperationCanceledException)
        {
            pill.Abandon(generation);
        }

        OnPropertyChanged(nameof(OfferTreatAsRna));
        StartRunCommand.NotifyCanExecuteChanged();
        return applied;
    }

    private string Format(string key, params object[] args)
        => string.Format(CultureInfo.CurrentCulture, _strings.GetString(key), args);
}
