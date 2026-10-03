using DnaEntropyGraph.Core.Inputs;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests.Inputs;

/// <summary>
/// Issue #479 done-when 3: the worker's own validation fixtures (read IN PLACE from
/// <c>worker/tests/data</c>, one source of truth, never copied) are run through
/// <see cref="InputFileValidator" /> and must agree with the worker.
///
/// The worker's pytest suite (<c>test_fuzz_readers.py</c>) only asserts "no raw exception",
/// not a verdict, so the verdicts below were MEASURED 2026-10-02 by running the real
/// <c>dna_entropy.readers.input.load_input</c> on every fixture (policy keep, and error) in
/// <c>worker\.venv</c>. This table is a snapshot of that run, not a second opinion: re-measure
/// it when a fixture is added (the "every fixture is in the table" test forces that).
///
/// Direction of safety: this validator is a pre-flight filter. Accepting something the worker
/// later refuses costs one wasted VM; refusing something the worker accepts blocks a
/// legitimate run. Both are listed explicitly in <see cref="Divergences" />, never hidden.
/// </summary>
public sealed class InputFileValidatorWorkerParityTests
{
    private sealed record Expect(bool PythonAccepts, InputProblemCode? Code = null, int? Position = null);

    private static readonly Dictionary<string, Expect> Table = new()
    {
        // accepted by load_input
        ["multi.gb"] = new(true),
        ["out_of_range.gb"] = new(true),
        ["prokaryotic_demo.fasta"] = new(true),
        ["sample.fasta"] = new(true),
        ["sample.gb"] = new(true),
        ["spliced.gb"] = new(true),
        ["malformed/fasta_crlf_and_lonecr_mixed.fasta"] = new(true),
        ["malformed/fasta_extremely_long_header.fasta"] = new(true),
        ["malformed/fasta_lowercase_ambiguity_mix.fasta"] = new(true),
        ["malformed/genbank_bad_coordinates.gb"] = new(true),
        ["malformed/genbank_lone_cr.gb"] = new(true),
        ["malformed/genbank_truncated_mid_origin.gb"] = new(true),

        // refused: no usable record
        ["malformed/fasta_binary_garbage.fasta"] = new(false, InputProblemCode.NoRecords),
        ["malformed/fasta_empty.fasta"] = new(false, InputProblemCode.NoRecords),
        ["malformed/fasta_gzip_magic_bytes.fasta"] = new(false, InputProblemCode.NoRecords),
        ["malformed/fasta_header_only_no_newline.fasta"] = new(false, InputProblemCode.NoRecords),
        ["malformed/fasta_html_error_page.fasta"] = new(false, InputProblemCode.NoRecords),
        ["malformed/fasta_no_header.fasta"] = new(false, InputProblemCode.NoRecords),
        ["malformed/fasta_only_greater_thans.fasta"] = new(false, InputProblemCode.NoRecords),
        ["malformed/fasta_only_whitespace.fasta"] = new(false, InputProblemCode.NoRecords),
        ["malformed/fasta_utf16_truncated.fasta"] = new(false, InputProblemCode.NoRecords),
        ["malformed/genbank_binary_garbage.gb"] = new(false, InputProblemCode.NoRecords),
        ["malformed/genbank_empty.gb"] = new(false, InputProblemCode.NoRecords),
        ["malformed/genbank_fasta_content.gb"] = new(false, InputProblemCode.NoRecords),
        ["malformed/genbank_html_error_page.gb"] = new(false, InputProblemCode.NoRecords),
        ["malformed/genbank_missing_locus.gb"] = new(false, InputProblemCode.NoRecords),
        ["malformed/genbank_only_whitespace.gb"] = new(false, InputProblemCode.NoRecords),
        ["malformed/genbank_utf16_truncated.gb"] = new(false, InputProblemCode.NoRecords),
        // Python refuses these for Biopython-specific reasons (a parse error); the C# reader finds
        // no ORIGIN block and also refuses. Same verdict, different reason text.
        ["malformed/genbank_missing_origin.gb"] = new(false, InputProblemCode.NoRecords),
        // Feature-table refusals (#548): Biopython's own scanner raises on these two shapes.
        ["malformed/genbank_qualifier_missing_slash.gb"] = new(false, InputProblemCode.NoRecords),
        ["malformed/genbank_feature_line_shorter_than_qualifier_indent.gb"] = new(false, InputProblemCode.NoRecords),
        ["malformed/genbank_truncated_mid_feature_table.gb"] = new(false, InputProblemCode.NoRecords),

        // refused: a bad base, same code and same 1-based position as Python's message
        ["malformed/fasta_mixed_garbage_bases.fasta"] = new(false, InputProblemCode.InvalidCharacter, 5),
        ["malformed/fasta_null_bytes.fasta"] = new(false, InputProblemCode.InvalidCharacter, 5),
        ["malformed/genbank_null_bytes.gb"] = new(false, InputProblemCode.InvalidCharacter, 5),
        ["malformed/fasta_only_digits_and_spaces.fasta"] = new(false, InputProblemCode.EmptySequence),

        // Biopython folds the second LOCUS line into the first record's ORIGIN data (it only ends a
        // record at "//"): "Invalid character 'E' at position 7". GenBankLite now does the same.
        ["malformed/genbank_two_locus_no_separator.gb"] = new(false, InputProblemCode.InvalidCharacter, 7),
    };

    /// <summary>
    /// Python refuses, C# ACCEPTS (a false accept: the worker still catches it, at the cost of a
    /// VM). These files are NOT in <see cref="Table" />; <c>Known_false_accepts_are_still_accepted_here</c>
    /// asserts the C# side so it goes red the day someone closes the gap.
    /// </summary>
    private static readonly Dictionary<string, string> FalseAccepts = new();

    private static string WorkerData(string relative) =>
        Path.Combine(ContractFixtures.RepoRoot, "worker", "tests", "data", relative.Replace('/', Path.DirectorySeparatorChar));

    public static IEnumerable<object[]> Fixtures() => Table.Keys.OrderBy(k => k, StringComparer.Ordinal).Select(k => new object[] { k });

    [Fact]
    public void Every_fixture_on_disk_is_in_the_table_and_the_corpus_is_not_vacuous()
    {
        var root = WorkerData(string.Empty).TrimEnd(Path.DirectorySeparatorChar);
        // Only files with a sequence-file extension are fixtures; a README or helper file dropped
        // into the folder must not fail this.
        string[] fixtureExtensions = [".gb", ".gbk", ".fasta", ".fa", ".fna", ".ffn"];
        var onDisk = Directory.GetFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(p => fixtureExtensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))
            .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/'))
            .ToList();

        onDisk.Count.ShouldBeGreaterThanOrEqualTo(30);
        onDisk.ShouldBe(Table.Keys.Concat(FalseAccepts.Keys), ignoreOrder: true, "a new/removed worker fixture needs its verdict re-measured and added here");
    }

    [Fact]
    public void Known_false_accepts_are_still_accepted_here_and_each_has_a_reason()
    {
        foreach (var (relative, reason) in FalseAccepts)
        {
            reason.Length.ShouldBeGreaterThan(20);
            var result = InputFileValidator.Validate(WorkerData(relative), InputFormat.Auto, AmbiguityPolicy.Keep, treatAsRna: false);
            result.IsValid.ShouldBeTrue($"{relative} is now refused here: delete it from FalseAccepts and add it to Table with the code");
        }
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Verdict_and_first_problem_agree_with_the_worker_under_the_keep_policy(string relative)
    {
        var expect = Table[relative];
        var result = InputFileValidator.Validate(WorkerData(relative), InputFormat.Auto, AmbiguityPolicy.Keep, treatAsRna: false);

        result.IsValid.ShouldBe(expect.PythonAccepts, $"{relative}: {result.Problem?.Code} {result.Problem?.Detail}");
        if (expect.Code is { } code)
        {
            result.Problem!.Code.ShouldBe(code, relative);
        }
        if (expect.Position is { } position)
        {
            result.Problem!.Position.ShouldBe(position, relative);
        }
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Verdict_agrees_under_the_error_policy_too(string relative)
    {
        var result = InputFileValidator.Validate(WorkerData(relative), InputFormat.Auto, AmbiguityPolicy.Error, treatAsRna: false);

        // Measured: the only fixture whose verdict changes under 'error' is the IUPAC-code mix,
        // which Python refuses at position 5 (the N).
        if (relative == "malformed/fasta_lowercase_ambiguity_mix.fasta")
        {
            result.Problem!.Code.ShouldBe(InputProblemCode.AmbiguityRefused);
            result.Problem.Position.ShouldBe(5);
            return;
        }

        result.IsValid.ShouldBe(Table[relative].PythonAccepts, relative);
    }

    [Fact]
    public void Accepted_fixtures_report_the_record_lengths_the_worker_reports()
    {
        // Python load_input contig lengths, measured 2026-10-02.
        (string File, int Records, long Total)[] cases =
        [
            ("multi.gb", 2, 58 + 57),
            ("sample.gb", 1, 126),
            ("sample.fasta", 1, 201),
            ("prokaryotic_demo.fasta", 1, 714),
            ("spliced.gb", 1, 200),
            ("out_of_range.gb", 1, 60),
            ("malformed/fasta_crlf_and_lonecr_mixed.fasta", 2, 8 + 4),
            ("malformed/genbank_truncated_mid_origin.gb", 1, 16),
        ];

        foreach (var (file, records, total) in cases)
        {
            var result = InputFileValidator.Validate(WorkerData(file), InputFormat.Auto, AmbiguityPolicy.Keep, treatAsRna: false);
            result.IsValid.ShouldBeTrue(file);
            result.RecordCount.ShouldBe(records, file);
            result.TotalLength.ShouldBe(total, file);
        }
    }
}
