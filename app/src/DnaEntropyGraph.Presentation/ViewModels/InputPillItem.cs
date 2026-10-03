using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Inputs;
using DnaEntropyGraph.Presentation.Services;

namespace DnaEntropyGraph.Presentation.ViewModels;

/// <summary>
/// One file (or pasted sequence) on the New run page and what the local validator found in it (issue #63):
/// kind, records, bases, genes, the validator's notices verbatim, and a problem named in <c>Resources.resw</c> copy.
/// </summary>
public sealed partial class InputPillItem : ObservableObject
{
    private readonly IStringResourceProvider _strings;

    public InputPillItem(string path, bool isPasted, IStringResourceProvider strings)
    {
        Path = path;
        IsPasted = isPasted;
        _strings = strings;
        DisplayName = isPasted ? strings.GetString("NewRunPastedName") : System.IO.Path.GetFileName(path);
    }

    public string Path { get; }

    public bool IsPasted { get; }

    public string DisplayName { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValid))]
    private bool _isChecking = true;

    [ObservableProperty]
    private string _kindText = string.Empty;

    [ObservableProperty]
    private string _summaryText = string.Empty;

    [ObservableProperty]
    private string _noticesText = string.Empty;

    [ObservableProperty]
    private string _errorText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValid))]
    private bool _hasError;

    /// <summary>Total bases in the file when the last check found it valid; null while checking or when there is a problem. The cost estimate (#98) is sized from it.</summary>
    public long? TotalBases { get; private set; }

    /// <summary>True when the only thing wrong is a U: the page offers Treat as RNA instead of an error.</summary>
    public bool NeedsRnaChoice { get; private set; }

    public bool HasNotices => NoticesText.Length > 0;

    /// <summary>True once checked with no problem. A pill still being checked is not valid.</summary>
    public bool IsValid => !IsChecking && !HasError;

    partial void OnNoticesTextChanged(string value) => OnPropertyChanged(nameof(HasNotices));

    private int _generation;

    /// <summary>The problem the last finished check found, kept so the page can offer a fix; null when valid or unchecked.</summary>
    public InputProblem? Problem { get; private set; }

    /// <summary>
    /// Marks the pill as being checked (again) and returns this check's number. Only the check holding the latest
    /// number may report (<see cref="Apply"/>, <see cref="Abandon"/>), so an older, slower check never overwrites a newer one.
    /// </summary>
    public int BeginChecking()
    {
        IsChecking = true;
        return ++_generation;
    }

    /// <summary>Ends whatever check is the pill's newest, the same way <see cref="Abandon(int)"/> does.</summary>
    public void AbandonLatest() => Abandon(_generation);

    /// <summary>Ends a check that was cancelled: the pill leaves the checking state with one action for the user. Ignored when a newer check has started.</summary>
    public void Abandon(int generation)
    {
        if (generation != _generation)
        {
            return;
        }

        Problem = null;
        TotalBases = null;
        KindText = string.Empty;
        SummaryText = string.Empty;
        NoticesText = string.Empty;
        NeedsRnaChoice = false;
        ErrorText = _strings.GetString("NewRunPillCheckStopped");
        HasError = true;
        IsChecking = false;
    }

    /// <summary>Shows what the validator found; ignored (false) when a newer check has started. Notices are <c>Resources.resw</c> copy chosen by code; a problem is too.</summary>
    public bool Apply(InputValidationResult result, int generation)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (generation != _generation)
        {
            return false;
        }

        var kind = KindFor(result);
        KindText = kind is null ? string.Empty : _strings.GetString(kind switch
        {
            InputKind.GenBank => "NewRunPillKind_GenBank",
            InputKind.Fasta => "NewRunPillKind_Fasta",
            _ => "NewRunPillKind_Paste",
        });
        // A failed file stops at its first problem, so its counts would be partial: show none.
        SummaryText = result.IsValid ? Summary(result) : string.Empty;
        NoticesText = string.Join('\n', result.NoticeCodes.Select(NoticeText).OfType<string>());
        Problem = result.Problem;
        TotalBases = result.IsValid ? result.TotalLength : null;

        var problem = result.Problem;
        NeedsRnaChoice = problem?.Code == InputProblemCode.RnaNotAllowed;
        HasError = problem is not null;
        ErrorText = problem is null ? string.Empty : NeedsRnaChoice ? _strings.GetString("NewRunPillRnaNotice") : ProblemText(problem);
        IsChecking = false;
        return true;
    }

    /// <summary>The kind to show. A file that could not be read has no sniffed kind, so its extension decides, or nothing is shown.</summary>
    private InputKind? KindFor(InputValidationResult result)
    {
        if (result.IsValid || result.Kind != InputKind.Paste)
        {
            return result.Kind;
        }

        return IsPasted ? InputKind.Paste : SequenceSniffer.DetectKindByExtension(Path);
    }

    private string? NoticeText(InputNotice notice)
    {
        var key = InputNoticeCopy.KeyFor(notice.Code, notice.Count);
        return key is null ? null : Format(key, notice.Count, notice.Other);
    }

    private string Summary(InputValidationResult result)
    {
        if (result.RecordCount == 0)
        {
            return string.Empty;
        }

        var parts = new List<string>
        {
            result.RecordCount == 1
                ? _strings.GetString("NewRunPillRecordsOne")
                : Format("NewRunPillRecordsMany", result.RecordCount),
            Format("NewRunPillBases", result.TotalLength),
        };
        if (result.Kind == InputKind.GenBank)
        {
            parts.Add(result.GeneCount == 1 ? _strings.GetString("NewRunPillGenesOne") : Format("NewRunPillGenesMany", result.GeneCount));
        }

        return string.Join(", ", parts);
    }

    private string ProblemText(InputProblem problem)
    {
        var message = _strings.GetString(RunErrorCodes.ResourceKey(InputProblemErrorCodes.For(problem.Code)));
        return (problem.RecordIndex, problem.Position) switch
        {
            (int record, int position) => message + " " + Format("NewRunPillWhereRecordBase", record, position),
            (int record, null) => message + " " + Format("NewRunPillWhereRecord", record),
            _ => message,
        };
    }

    private string Format(string key, params object[] args)
        => string.Format(CultureInfo.CurrentCulture, _strings.GetString(key), args);
}
