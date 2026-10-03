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
public sealed class RunListItem
{
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
        IAsyncRelayCommand removeCommand)
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

    public IAsyncRelayCommand OpenCommand { get; }

    public IAsyncRelayCommand RerunCommand { get; }

    public IAsyncRelayCommand RedownloadCommand { get; }

    public IAsyncRelayCommand DeleteCloudCommand { get; }

    public IAsyncRelayCommand DeleteLocalCommand { get; }

    public IAsyncRelayCommand RemoveCommand { get; }
}
