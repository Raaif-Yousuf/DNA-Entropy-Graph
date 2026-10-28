using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;

namespace DnaEntropyGraph.Presentation.ViewModels;

/// <summary>The status groups the Runs page filters by (issue #101).</summary>
public enum RunStatusFilter
{
    All,
    Active,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>One entry of the status filter box: the value and the words the user sees.</summary>
public sealed record RunStatusFilterOption(RunStatusFilter Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One day of runs on the Runs page, newest day first.</summary>
public sealed record RunDayGroup(string Header, IReadOnlyList<RunListItem> Items);

/// <summary>
/// One row of the Runs page. Its commands are built by <see cref="HistoryViewModel"/> and close over this
/// run, so a DataTemplate can bind them without reaching back to the page's ViewModel.
/// </summary>
public sealed partial class RunListItem : ObservableObject
{
    /// <summary>How many points the card's sparkline is drawn from (issue #118); a longer track is averaged down to this.</summary>
    public const int SparklinePoints = 48;

    /// <summary>The run's entropy along its first contig, small enough to draw on a card; null until it is read, and for a run with no track.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSparkline))]
    private float[]? _sparkline;

    public bool HasSparkline => Sparkline is { Length: > 1 };

    public RunListItem(
        RunRecord run,
        string title,
        string statusText,
        RunStatusFilter status,
        string detailText,
        bool hasLocalFiles,
        bool cloudAvailable,
        bool canDeleteCloud,
        string? deleteCloudHint,
        string? reasonText,
        IAsyncRelayCommand openCommand,
        IAsyncRelayCommand rerunCommand,
        IAsyncRelayCommand redownloadCommand,
        IAsyncRelayCommand deleteCloudCommand,
        IAsyncRelayCommand deleteLocalCommand,
        IAsyncRelayCommand removeCommand,
        IRelayCommand? copyOwnerRequestCommand = null)
    {
        Run = run;
        Title = title;
        StatusText = statusText;
        Status = status;
        DetailText = detailText;
        var finished = HistoryViewModel.IsFinished(run.Phase);
        HasLocalFiles = finished && hasLocalFiles;
        CanRedownload = finished && cloudAvailable;
        CanDeleteCloud = finished && cloudAvailable && canDeleteCloud;
        DeleteCloudHint = deleteCloudHint;
        ReasonText = reasonText;
        CanRemove = finished;
        OpenCommand = openCommand;
        RerunCommand = rerunCommand;
        RedownloadCommand = redownloadCommand;
        DeleteCloudCommand = deleteCloudCommand;
        DeleteLocalCommand = deleteLocalCommand;
        RemoveCommand = removeCommand;
        CopyOwnerRequestCommand = copyOwnerRequestCommand;
    }

    public RunRecord Run { get; }

    public string JobId => Run.JobId;

    public string Title { get; }

    public string StatusText { get; }

    public RunStatusFilter Status { get; }

    public string DetailText { get; }

    public bool HasLocalFiles { get; }

    public bool CanRedownload { get; }

    public bool CanDeleteCloud { get; }

    public bool CanRemove { get; }

    /// <summary>Why Delete cloud copy is unavailable, or null when it is available; shown as its tooltip.</summary>
    public string? DeleteCloudHint { get; }

    /// <summary>
    /// Why the run failed, or what to check on a run that finished with a warning (issue #459): the plain-language
    /// <c>RunError_*</c> text for its code, which names one action. Null when there is nothing to say.
    /// Never the raw <c>RunRecord.ErrorDetail</c>, which is for the diagnostics zip only.
    /// </summary>
    public string? ReasonText { get; }

    public bool HasReason => ReasonText is not null;

    /// <summary>"Preset: Plasmid QC" for a run started from an option preset (issue #136); null for a run set by hand.</summary>
    public string? PresetText { get; init; }

    /// <summary>Issue #111: the run was found in the results bucket (made on another computer); only Download again and Remove from list act on it.</summary>
    public bool IsImported { get; init; }

    public string? ImportedText { get; init; }

    public bool HasImportedText => ImportedText is not null;

    /// <summary>"Download again", or "Download partial results" for a cancelled run (issue #717: what the worker uploaded before it stopped).</summary>
    public string RedownloadLabel { get; init; } = string.Empty;

    public bool HasPreset => PresetText is not null;

    public IAsyncRelayCommand OpenCommand { get; }

    public IAsyncRelayCommand RerunCommand { get; }

    public IAsyncRelayCommand RedownloadCommand { get; }

    public IAsyncRelayCommand DeleteCloudCommand { get; }

    public IAsyncRelayCommand DeleteLocalCommand { get; }

    public IAsyncRelayCommand RemoveCommand { get; }

    /// <summary>
    /// Issue #604: copies the request that asks the project owner to grant the Service Account User role on the worker
    /// account. Null (and <see cref="HasOwnerRequest"/> false) unless the run failed for exactly that reason.
    /// </summary>
    public IRelayCommand? CopyOwnerRequestCommand { get; }

    public bool HasOwnerRequest => CopyOwnerRequestCommand is not null;

    /// <summary>Opens this run's folder on Google's storage page (the action after a delete this app refused to do); set by the list.</summary>
    public IRelayCommand? OpenStoragePageCommand { get; init; }

    /// <summary>True after a Delete cloud copy that was refused as not this installation's, so the Open storage page button is offered.</summary>
    public bool ShowStoragePage { get; init; }
}
