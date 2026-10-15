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

    /// <summary>
    /// A pill for an NCBI accession the worker fetches on the VM (issue #684). It is a GenBank record with no file on this PC, so it is
    /// valid at once (the accession was checked by <see cref="AccessionInput"/> before the pill was made) and has no bases to size an estimate from.
    /// </summary>
    public static InputPillItem ForAccession(string accession, string? range, IStringResourceProvider strings)
    {
        ArgumentNullException.ThrowIfNull(accession);
        var pill = new InputPillItem(string.Empty, false, strings)
        {
            Accession = accession,
            AccessionRange = range,
            DisplayName = range is null ? accession : accession + " (" + range + ")",
        };
        pill.Kind = InputKind.GenBank;
        pill.KindText = strings.GetString("NewRunPillKind_Ncbi");
        pill.SummaryText = strings.GetString("NewRunPillAccessionSummary");
        pill.IsChecking = false;
        return pill;
    }

    public string Path { get; }

    /// <summary>The NCBI accession this pill fetches, or null for a file or pasted sequence.</summary>
    public string? Accession { get; private init; }

    /// <summary>The <c>start-end</c> part of the record, or null for all of it.</summary>
    public string? AccessionRange { get; private init; }

    public bool IsAccession => Accession is not null;

    public bool IsPasted { get; }

    public string DisplayName { get; private init; }

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

    /// <summary>The kind the last check found (or, for a file that could not be read, its extension said); null while unknown. The output choices (#103) follow it.</summary>
    public InputKind? Kind { get; private set; }

    /// <summary>True when the only thing wrong is a U: the page offers Treat as RNA instead of an error.</summary>
    public bool NeedsRnaChoice { get; private set; }

    public bool HasNotices => NoticesText.Length > 0;

    /// <summary>True once checked with no problem. A pill still being checked is not valid.</summary>
    public bool IsValid => !IsChecking && !HasError;

    /// <summary>The GFF3 paired with this FASTA (issue #686), or null. The run sends it as its annotation, and the worker uses its genes instead of finding them.</summary>
    public string? AnnotationPath { get; private set; }

    public bool HasAnnotation => AnnotationPath is not null;

    /// <summary>"Genes from name.gff3: 12 genes. The gene finder (Prodigal) is skipped." Empty with no GFF3.</summary>
    [ObservableProperty]
    private string _annotationText = string.Empty;

    /// <summary>What the GFF3 check noticed and carried on past (lines skipped, names with no sequence); empty when it had nothing to say.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAnnotationNotices))]
    private string _annotationNoticesText = string.Empty;

    public bool HasAnnotationNotices => AnnotationNoticesText.Length > 0;

    /// <summary>Shows a GFF3 that fits this FASTA: its gene count and the checker's notices. Only a pair with no problem is ever applied.</summary>
    public void ApplyAnnotation(string annotationPath, AnnotationCheckResult result)
    {
        ArgumentNullException.ThrowIfNull(annotationPath);
        ArgumentNullException.ThrowIfNull(result);
        AnnotationPath = annotationPath;
        var genes = result.GeneCount == 1 ? _strings.GetString("NewRunPillGenesOne") : Format("NewRunPillGenesMany", result.GeneCount);
        AnnotationText = Format("NewRunPillAnnotationLine", System.IO.Path.GetFileName(annotationPath), genes);
        AnnotationNoticesText = string.Join('\n', result.NoticeCodes.Select(NoticeText).OfType<string>());
        OnPropertyChanged(nameof(HasAnnotation));
    }

    /// <summary>Takes the GFF3 off this FASTA; the FASTA stays.</summary>
    public void ClearAnnotation()
    {
        AnnotationPath = null;
        AnnotationText = string.Empty;
        AnnotationNoticesText = string.Empty;
        OnPropertyChanged(nameof(HasAnnotation));
    }
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

    /// <summary>Ends the checking state of a pill no check ever began on (an add cancelled first). Ignored once any check has begun: that check owns the state, and it is not the caller's to end.</summary>
    public void AbandonIfNeverChecked() => Abandon(0);

    /// <summary>Ends a check that was cancelled: the pill leaves the checking state with one action for the user. Ignored when a newer check has started.</summary>
    public void Abandon(int generation)
    {
        if (generation != _generation)
        {
            return;
        }

        Problem = null;
        TotalBases = null;
        Kind = null;
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
        Kind = kind;
        KindText = kind is null ? string.Empty : _strings.GetString(kind switch
        {
            InputKind.GenBank => "NewRunPillKind_GenBank",
            InputKind.Fasta => "NewRunPillKind_Fasta",
            InputKind.Embl => "NewRunPillKind_Embl",
            InputKind.SnapGene => "NewRunPillKind_SnapGene",
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
        if (result.Kind is InputKind.GenBank or InputKind.Embl or InputKind.SnapGene)
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
