namespace DnaEntropyGraph.Core.Inputs;

/// <summary>
/// What a non-fatal validation notice is about. The English text the readers and the validator produce stays a
/// log and worker-parity detail; the UI shows <c>Resources.resw</c> copy keyed <c>NewRunNotice_&lt;code&gt;</c>
/// (Hard Rule 13), so each value here needs one such key (a Guards test checks).
/// </summary>
public enum InputNoticeCode
{
    /// <summary>A FASTA record had a header and no sequence and was left out. <see cref="InputNotice.Count"/> is its 1-based position.</summary>
    RecordSkippedNoSequence,

    /// <summary>Records with a bare <c>&gt;</c> and no name. Count is how many.</summary>
    EmptyHeaders,

    /// <summary>Record ids that repeat across records. Count is how many ids.</summary>
    RepeatedIds,

    /// <summary>A multi-record FASTA was read. Count is the number of records (the pill already shows it).</summary>
    RecordsRead,

    /// <summary>A GenBank record had no sequence and was left out. Count is its 1-based position.</summary>
    GenBankRecordSkippedNoSequence,

    /// <summary>A GenBank file was read. Count is the records, <see cref="InputNotice.Other"/> the gene and CDS features (the pill already shows both).</summary>
    GenBankRead,

    /// <summary>U was read as T because the run treats input as RNA. Count is how many.</summary>
    RnaConverted,

    /// <summary>Ambiguity codes (N, R, Y ...) were replaced with N. Count is how many.</summary>
    AmbiguityMasked,

    /// <summary>Ambiguity codes (N, R, Y ...) were kept. Count is how many.</summary>
    AmbiguityKept,

    /// <summary>The sequence is shorter than the model needs to be reliable. Count is its length.</summary>
    ShortSequence,

    /// <summary>A leading <c>&gt;</c> line was treated as a name and ignored.</summary>
    LeadingHeaderIgnored,

    /// <summary>Digits (such as line numbers) were removed. Count is how many.</summary>
    DigitsRemoved,
}

/// <summary>A notice's machine code and the numbers it mentions (never sequence text, a name or a path).</summary>
public sealed record InputNotice(InputNoticeCode Code, long Count = 0, long Other = 0);
