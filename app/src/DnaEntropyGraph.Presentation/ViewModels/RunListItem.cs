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
        IRelayCommand? copyOwnerRequestCommand = null,
        IAsyncRelayCommand? waitForGpuCommand = null)
    {
        Run = run;
        Title = title;
        StatusText = statusText;
        Status = status;
        DetailText = detailText;
        var finished = HistoryViewModel.IsFinished(run.Phase);
        HasLocalFiles = finished && hasLocalFiles;
        CanDeleteLocal = HasLocalFiles && !run.Imported;
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
        WaitForGpuCommand = waitForGpuCommand;
    }

    public RunRecord Run { get; }

    public string JobId => Run.JobId;

    public string Title { get; }

    public string StatusText { get; }

    public RunStatusFilter Status { get; }

    public string DetailText { get; }

    public bool HasLocalFiles { get; }

    /// <summary>Whether Delete files on this PC is on: the run has files here and the app wrote them. An imported run's folder is the user's own (Hard Rule 14).</summary>
    public bool CanDeleteLocal { get; }

    /// <summary>Why Delete files on this PC is off for an imported run (its tooltip); null when it is not off for that reason.</summary>
    public string? DeleteLocalHint { get; init; }

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

    /// <summary>The run's cost line (issues #100, #557), the same booked figure as the Results header; null when the run booked nothing.</summary>
    public string? CostText { get; init; }

    public bool HasCost => CostText is not null;

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

    /// <summary>The run's tags (issue #141), shown as chips; empty for a run with none.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    public bool HasTags => Tags.Count > 0;

    /// <summary>The run's note (issue #141); null when there is none.</summary>
    public string? NotesText { get; init; }

    public bool HasNotes => NotesText is not null;

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

    /// <summary>
    /// Issue #92: "Wait for a graphics card", the action of a run that failed because Google had none free: starts the same run again, queued by Google until a card is free.
    /// Null (and <see cref="HasWaitForGpu"/> false) for every other run, and for one that already waited.
    /// </summary>
    public IAsyncRelayCommand? WaitForGpuCommand { get; }

    public bool HasWaitForGpu => WaitForGpuCommand is not null;
}
