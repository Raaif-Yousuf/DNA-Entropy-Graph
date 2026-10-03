namespace DnaEntropyGraph.Core.Inputs;

/// <summary>Why a staged input file cannot be run. One machine code per cause; the caller maps it to a run error code and a Resources.resw key.</summary>
public enum InputProblemCode
{
    /// <summary>The file is missing, locked or unreadable.</summary>
    FileUnreadable,

    /// <summary>The file is larger than <see cref="InputLimits.MaxFileBytes" />; it was not read.</summary>
    FileTooLarge,

    /// <summary>No usable record: no FASTA header, no GenBank LOCUS/ORIGIN block, or every record empty.</summary>
    NoRecords,

    /// <summary>A record has no bases left after removing whitespace and digits.</summary>
    EmptySequence,

    /// <summary>A character that is not A, C, G, T or an IUPAC ambiguity code (including any non-ASCII letter).</summary>
    InvalidCharacter,

    /// <summary>An IUPAC ambiguity code while the run's policy is <see cref="AmbiguityPolicy.Error" />.</summary>
    AmbiguityRefused,

    /// <summary>A U in the sequence while the run does not treat input as RNA.</summary>
    RnaNotAllowed,

    /// <summary>The file is not valid UTF-8 (Windows-1252 and friends); it contains undecodable bytes.</summary>
    UndecodableText,

    /// <summary>One record is longer than <see cref="InputLimits.MaxTotalLen" />.</summary>
    RecordTooLong,

    /// <summary>The records together exceed <see cref="InputLimits.MaxTotalLen" /> (the worker's whole-input cap).</summary>
    TotalTooLong,

    /// <summary>The file's bases exceed the manifest's batch budget <see cref="InputLimits.MaxBatchNt" />.</summary>
    BatchBudgetExceeded,
}

/// <summary>
/// The first thing wrong with an input file.
/// </summary>
/// <param name="Code">Machine code.</param>
/// <param name="RecordIndex">1-based record position in the file as the user sees it (a record the reader skipped still counts); null when the problem is about the whole file.</param>
/// <param name="Position">1-based base offset in the cleaned record (whitespace and digits removed, counted in code points like the worker); null when there is no single position.</param>
/// <param name="Detail">English diagnostic text for logs and tests, never shown to the user (user text comes from Resources.resw). It names characters and positions only: never the file name or a header. Prefer logging Code, RecordIndex and Position.</param>
public sealed record InputProblem(InputProblemCode Code, int? RecordIndex, int? Position, string Detail);

/// <summary>Size caps. Defaults mirror the worker: <c>config.DEFAULT_MAX_TOTAL_LEN</c> and <c>batch_limits.DEFAULT_MAX_TOTAL_NT</c> (= the manifest's <c>limits.maxTotalNt</c>).</summary>
public sealed record InputLimits
{
    /// <summary>The worker's whole-input cap, also used as each record's cap (the worker passes it as max_len).</summary>
    public long MaxTotalLen { get; init; } = 10_000_000;

    /// <summary>Manifest <c>limits.maxTotalNt</c>: bases allowed across the batch. A caller validating several files passes the remaining budget.</summary>
    public long MaxBatchNt { get; init; } = 20_000_000;

    /// <summary>
    /// Refuse to load a file larger than this into memory (not a worker rule; protects the app). 64 MiB covers the
    /// 20 Mnt batch budget (FASTA about 1.02 bytes per base, GenBank sequence about 1.25) with room for a feature table.
    /// Peak memory is roughly 7 times the file (bytes, UTF-16 text, line split, cleaned copies): about 450 MB at the
    /// cap, versus about 1.8 GB at the former 256 MiB.
    /// </summary>
    public long MaxFileBytes { get; init; } = 64L * 1024 * 1024;
}

/// <summary>Outcome of <see cref="InputFileValidator.Validate" />.</summary>
public sealed record InputValidationResult(
    InputProblem? Problem,
    InputKind Kind,
    int RecordCount,
    long TotalLength,
    IReadOnlyList<string> Notices)
{
    public bool IsValid => Problem is null;

    /// <summary>Gene features a GenBank file already carries (a pill shows them); 0 for FASTA and plain text.</summary>
    public int GeneCount { get; init; }
}

/// <summary>
/// Validates a staged input file with a run's options before any cloud resource exists (Hard
/// Rule 2, issue #479). It composes <see cref="SequenceSniffer" />, <see cref="FastaLite" /> /
/// <see cref="GenBankLite" /> and <see cref="SequenceValidator" /> in the order of the worker's
/// <c>readers/input.py::load_input</c>: detect the format, read every record, validate each
/// record, then enforce the whole-input cap. It returns the first problem and never throws for a
/// bad input. It is a pre-flight filter, not the source of truth: the worker re-validates.
/// </summary>
public static class InputFileValidator
{
    public static InputValidationResult Validate(
        string path,
        InputFormat format,
        AmbiguityPolicy ambiguityPolicy,
        bool treatAsRna,
        InputLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        limits ??= new InputLimits();

        // Order matters: existence and size are checked from file metadata BEFORE any byte is read
        // (a file over the cap is never loaded), and the format comes from the option or the
        // extension; only an unknown extension needs the content, sniffed after the capped read.
        string text;
        InputKind kind;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return Fail(InputKind.Paste, InputProblemCode.FileUnreadable, null, null, "The input file does not exist.");
            }
            InputKind? known = format switch
            {
                InputFormat.GenBank => InputKind.GenBank,
                InputFormat.Fasta => InputKind.Fasta,
                InputFormat.Plain => InputKind.Paste,
                _ => SequenceSniffer.DetectKindByExtension(path),
            };
            if (info.Length > limits.MaxFileBytes)
            {
                // Kind is Paste (undecided) when only the content could have said.
                return Fail(known ?? InputKind.Paste, InputProblemCode.FileTooLarge, null, null, $"The input file is {info.Length} bytes, over the {limits.MaxFileBytes} byte limit.");
            }
            text = TextDecoder.ReadText(path);
            kind = known ?? SequenceSniffer.DetectKindFromContent(text);
        }
        catch (OutOfMemoryException)
        {
            return Fail(InputKind.Paste, InputProblemCode.FileTooLarge, null, null, "Not enough memory to check this file.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            // Not ex.Message: it can carry the path.
            return Fail(InputKind.Paste, InputProblemCode.FileUnreadable, null, null, "The input file could not be read.");
        }
        var notices = new List<string>();
        List<(string Seq, int Index)> records;
        var geneCount = 0;
        try
        {
            switch (kind)
            {
                case InputKind.GenBank:
                    var gb = GenBankLite.Read(text);
                    notices.AddRange(gb.Notices);
                    geneCount = gb.Records.Sum(r => r.GeneCount);
                    records = gb.Records.Select(r => (r.Seq, r.SourceIndex)).ToList();
                    break;
                case InputKind.Fasta:
                    var fa = FastaLite.Read(text);
                    notices.AddRange(fa.Notices);
                    records = fa.Records.Select(r => (r.Seq, r.SourceIndex)).ToList();
                    break;
                default:
                    records = [(text, 1)]; // plain text: one sequence, an optional leading '>' line is stripped by the validator
                    break;
            }
        }
        catch (Exception ex) when (ex is FastaReadException or GenBankReadException)
        {
            return Fail(kind, InputProblemCode.NoRecords, null, null, ex.Message, notices);
        }

        var maxLen = (int)Math.Min(limits.MaxTotalLen, int.MaxValue);
        long total = 0;
        foreach (var (seq, index) in records)
        {
            ValidatedSequence validated;
            try
            {
                validated = SequenceValidator.Validate(seq, maxLen, treatAsRna, SequenceValidator.DefaultMinLen, ambiguityPolicy);
            }
            catch (SequenceValidationException ex)
            {
                return Fail(kind, Map(ex.Reason), index, ex.Position, ex.Message, notices, records.Count, total);
            }

            notices.AddRange(validated.Notices);
            total += validated.Length;
            if (total > limits.MaxTotalLen)
            {
                return Fail(
                    kind,
                    InputProblemCode.TotalTooLong,
                    index,
                    null,
                    $"Combined input length {total} nt exceeds the whole-input cap of {limits.MaxTotalLen} nt.",
                    notices,
                    records.Count,
                    total);
            }
        }

        if (total > limits.MaxBatchNt)
        {
            return Fail(
                kind,
                InputProblemCode.BatchBudgetExceeded,
                null,
                null,
                $"{total} nt exceeds the batch budget of {limits.MaxBatchNt} nt.",
                notices,
                records.Count,
                total);
        }

        return new InputValidationResult(null, kind, records.Count, total, notices) { GeneCount = geneCount };
    }

    private static InputProblemCode Map(SequenceFailure reason) => reason switch
    {
        SequenceFailure.Rna => InputProblemCode.RnaNotAllowed,
        SequenceFailure.Empty => InputProblemCode.EmptySequence,
        SequenceFailure.Undecodable => InputProblemCode.UndecodableText,
        SequenceFailure.InvalidCharacter => InputProblemCode.InvalidCharacter,
        SequenceFailure.AmbiguityRefused => InputProblemCode.AmbiguityRefused,
        SequenceFailure.TooLong => InputProblemCode.RecordTooLong,
        _ => throw new InvalidOperationException($"SequenceValidator raised an unmapped failure reason '{reason}'."),
    };

    private static InputValidationResult Fail(
        InputKind kind,
        InputProblemCode code,
        int? recordIndex,
        int? position,
        string detail,
        IReadOnlyList<string>? notices = null,
        int recordCount = 0,
        long total = 0) =>
        new(new InputProblem(code, recordIndex, position, detail), kind, recordCount, total, notices ?? []);
}
