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

    /// <summary>True when the only thing wrong is a U: the page offers Treat as RNA instead of an error.</summary>
    public bool NeedsRnaChoice { get; private set; }

    public bool HasNotices => NoticesText.Length > 0;

    /// <summary>True once checked with no problem. A pill still being checked is not valid.</summary>
    public bool IsValid => !IsChecking && !HasError;

    partial void OnNoticesTextChanged(string value) => OnPropertyChanged(nameof(HasNotices));

    /// <summary>Marks the pill as being checked again (the options changed).</summary>
    public void BeginChecking() => IsChecking = true;

    /// <summary>Shows what the validator found. Notices are the validator's own text; a problem is copy from <c>Resources.resw</c>.</summary>
    public void Apply(InputValidationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        KindText = _strings.GetString(result.Kind switch
        {
            InputKind.GenBank => "NewRunPillKind_GenBank",
            InputKind.Fasta => "NewRunPillKind_Fasta",
            _ => "NewRunPillKind_Paste",
        });
        // A failed file stops at its first problem, so its counts would be partial: show none.
        SummaryText = result.IsValid ? Summary(result) : string.Empty;
        NoticesText = string.Join('\n', result.Notices);

        var problem = result.Problem;
        NeedsRnaChoice = problem?.Code == InputProblemCode.RnaNotAllowed;
        HasError = problem is not null;
        ErrorText = problem is null ? string.Empty : NeedsRnaChoice ? _strings.GetString("NewRunPillRnaNotice") : ProblemText(problem);
        IsChecking = false;
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
