using System.Text;
using DnaEntropyGraph.Core.Inputs;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests.Inputs;

/// <summary>
/// Issue #479 (core half): <see cref="InputFileValidator" /> composes the sniffer, the
/// readers and <see cref="SequenceValidator" /> into "validate this staged file with these run
/// options" and returns the FIRST problem with a machine code and a position. Nothing in the
/// run path calls it yet (the JobEngine / runner wiring is a separate lane).
/// </summary>
public sealed class InputFileValidatorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "deg-input-validator-" + Guid.NewGuid().ToString("N"));

    public InputFileValidatorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort: a leftover temp dir is not a test failure
        }
    }

    private string Write(string name, string content, Encoding? encoding = null)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, (encoding ?? new UTF8Encoding(false)).GetBytes(content));
        return path;
    }

    private static InputValidationResult Run(
        string path,
        InputFormat format = InputFormat.Auto,
        AmbiguityPolicy policy = AmbiguityPolicy.Keep,
        bool rna = false,
        InputLimits? limits = null) =>
        InputFileValidator.Validate(path, format, policy, rna, limits);

    // ---- the observable: an X at a known position -------------------------------------------

    [Fact]
    public void A_FASTA_with_an_X_returns_the_code_the_record_and_the_base_position()
    {
        var path = Write("bad.fasta", ">rec1\nACGTXACGTACGT\n");

        var result = Run(path);

        result.IsValid.ShouldBeFalse();
        result.Problem.ShouldNotBeNull();
        result.Problem.Code.ShouldBe(InputProblemCode.InvalidCharacter);
        result.Problem.RecordIndex.ShouldBe(1);
        result.Problem.Position.ShouldBe(5);
    }

    [Fact]
    public void The_record_index_is_the_position_in_the_file_even_when_an_empty_record_was_skipped()
    {
        var path = Write("skip.fasta", ">a\nACGTACGTAC\n>empty\n>c\nACGTXCGTAC\n");

        var problem = Run(path).Problem;

        problem.ShouldNotBeNull();
        problem.Code.ShouldBe(InputProblemCode.InvalidCharacter);
        problem.RecordIndex.ShouldBe(3); // records 1 (a), 2 (empty, skipped), 3 (c)
        problem.Position.ShouldBe(5);
    }

    [Fact]
    public void Only_the_first_problem_is_reported()
    {
        var path = Write("two.fasta", ">a\nACGTXCGTAC\n>b\nACGTUCGTAC\n");

        var problem = Run(path).Problem;

        problem.ShouldNotBeNull();
        problem.RecordIndex.ShouldBe(1);
        problem.Code.ShouldBe(InputProblemCode.InvalidCharacter);
    }

    // ---- accepted inputs ---------------------------------------------------------------------

    [Fact]
    public void A_clean_multi_record_FASTA_is_valid_and_counts_records_and_bases()
    {
        var path = Write("ok.fasta", ">a\nACGTACGTAC\n>b\nGGGGCCCCAA\nTTTT\n");

        var result = Run(path);

        result.IsValid.ShouldBeTrue();
        result.Problem.ShouldBeNull();
        result.Kind.ShouldBe(InputKind.Fasta);
        result.RecordCount.ShouldBe(2);
        result.TotalLength.ShouldBe(24);
    }

    [Fact]
    public void An_extensionless_file_is_routed_by_its_first_line()
    {
        var fasta = Write("noext", ">a\nACGTACGTAC\n");
        var plain = Write("plain.txt", "ACGT ACGT AC\n");

        Run(fasta).Kind.ShouldBe(InputKind.Fasta);
        var plainResult = Run(plain);
        plainResult.Kind.ShouldBe(InputKind.Paste);
        plainResult.IsValid.ShouldBeTrue();
        plainResult.TotalLength.ShouldBe(10);
    }

    [Fact]
    public void An_explicit_format_overrides_the_extension()
    {
        var path = Write("actually_fasta.gb", ">a\nACGTACGTAC\n");

        Run(path, InputFormat.Fasta).IsValid.ShouldBeTrue();
        Run(path, InputFormat.GenBank).Problem!.Code.ShouldBe(InputProblemCode.NoRecords);
    }

    // ---- ambiguity policy --------------------------------------------------------------------

    [Theory]
    [InlineData(AmbiguityPolicy.Keep, true)]
    [InlineData(AmbiguityPolicy.Mask, true)]
    [InlineData(AmbiguityPolicy.Error, false)]
    public void The_ambiguity_policy_decides_whether_an_N_is_refused(AmbiguityPolicy policy, bool valid)
    {
        var path = Write("n.fasta", ">a\nACGTNACGTAC\n");

        var result = Run(path, policy: policy);

        result.IsValid.ShouldBe(valid);
        if (!valid)
        {
            result.Problem!.Code.ShouldBe(InputProblemCode.AmbiguityRefused);
            result.Problem.RecordIndex.ShouldBe(1);
            result.Problem.Position.ShouldBe(5);
        }
    }

    [Fact]
    public void A_non_IUPAC_letter_is_refused_under_every_policy()
    {
        var path = Write("x.fasta", ">a\nACGTXACGTAC\n");

        foreach (var policy in Enum.GetValues<AmbiguityPolicy>())
        {
            Run(path, policy: policy).Problem!.Code.ShouldBe(InputProblemCode.InvalidCharacter);
        }
    }

    // ---- RNA ---------------------------------------------------------------------------------

    [Fact]
    public void RNA_is_refused_unless_the_run_treats_input_as_RNA()
    {
        var path = Write("rna.fasta", ">a\nACGUACGUAC\n");

        var refused = Run(path).Problem;
        refused.ShouldNotBeNull();
        refused.Code.ShouldBe(InputProblemCode.RnaNotAllowed);
        refused.Position.ShouldBe(4);

        Run(path, rna: true).IsValid.ShouldBeTrue();
    }

    // ---- limits ------------------------------------------------------------------------------

    [Fact]
    public void A_record_longer_than_the_whole_input_cap_is_refused_as_too_long_before_the_total_check()
    {
        var path = Write("long.fasta", ">a\n" + new string('A', 50) + "\n");

        var problem = Run(path, limits: new InputLimits { MaxTotalLen = 40 }).Problem;

        problem.ShouldNotBeNull();
        problem.Code.ShouldBe(InputProblemCode.RecordTooLong);
        problem.RecordIndex.ShouldBe(1);
    }

    [Fact]
    public void Records_each_under_the_cap_but_over_it_together_name_the_record_that_crosses_it()
    {
        var path = Write("sum.fasta", ">a\n" + new string('A', 30) + "\n>b\n" + new string('C', 30) + "\n");

        var problem = Run(path, limits: new InputLimits { MaxTotalLen = 40 }).Problem;

        problem.ShouldNotBeNull();
        problem.Code.ShouldBe(InputProblemCode.TotalTooLong);
        problem.RecordIndex.ShouldBe(2);
    }

    [Fact]
    public void A_file_over_the_manifest_batch_budget_is_refused()
    {
        var path = Write("batch.fasta", ">a\n" + new string('A', 30) + "\n");

        var problem = Run(path, limits: new InputLimits { MaxBatchNt = 20 }).Problem;

        problem.ShouldNotBeNull();
        problem.Code.ShouldBe(InputProblemCode.BatchBudgetExceeded);
    }

    [Fact]
    public void A_file_over_the_byte_cap_is_refused_without_reading_it()
    {
        // Auto + an unknown extension is the one route that reads content to pick a format. The
        // file starts with '>', so a sniff would report Fasta; Kind Paste (undecided) on the
        // refusal proves the size check ran before any content was read.
        var path = Write("big.txt", ">a\n" + new string('A', 200) + "\n");

        var result = Run(path, limits: new InputLimits { MaxFileBytes = 100 });

        result.Problem.ShouldNotBeNull();
        result.Problem.Code.ShouldBe(InputProblemCode.FileTooLarge);
        result.Kind.ShouldBe(InputKind.Paste);
    }

    [Fact]
    public void A_too_large_file_with_a_known_extension_still_reports_its_kind()
    {
        var path = Write("big.fasta", ">a\n" + new string('A', 200) + "\n");

        var result = Run(path, limits: new InputLimits { MaxFileBytes = 100 });

        result.Problem!.Code.ShouldBe(InputProblemCode.FileTooLarge);
        result.Kind.ShouldBe(InputKind.Fasta);
    }

    [Fact]
    public void The_default_byte_cap_is_64_MiB_which_covers_the_20_Mnt_batch_budget_with_annotation()
    {
        new InputLimits().MaxFileBytes.ShouldBe(64L * 1024 * 1024);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad\0path.fasta")]
    [InlineData("C:\\no\\such\\dir\\x.fasta")]
    public void An_unusable_path_is_a_FileUnreadable_problem_never_an_exception(string path)
    {
        Run(path).Problem!.Code.ShouldBe(InputProblemCode.FileUnreadable);
    }

    [Fact]
    public void The_default_limits_match_the_worker_and_the_manifest_builder()
    {
        // worker config.DEFAULT_MAX_TOTAL_LEN and batch_limits.DEFAULT_MAX_TOTAL_NT.
        new InputLimits().MaxTotalLen.ShouldBe(10_000_000);
        new InputLimits().MaxBatchNt.ShouldBe(20_000_000);
    }

    // ---- structural problems -----------------------------------------------------------------

    [Fact]
    public void A_missing_file_is_a_FileUnreadable_problem_not_an_exception()
    {
        Run(Path.Combine(_dir, "nope.fasta")).Problem!.Code.ShouldBe(InputProblemCode.FileUnreadable);
    }

    [Theory]
    [InlineData("empty.fasta", "")]
    [InlineData("headless.fasta", "ACGTACGT\n")]
    [InlineData("headeronly.fasta", ">only\n")]
    [InlineData("empty.gb", "")]
    [InlineData("noorigin.gb", "LOCUS       x    5 bp    DNA\nFEATURES             Location/Qualifiers\n//\n")]
    public void A_file_with_no_usable_records_is_NoRecords(string name, string content)
    {
        var problem = Run(Write(name, content)).Problem;

        problem.ShouldNotBeNull();
        problem.Code.ShouldBe(InputProblemCode.NoRecords);
        problem.RecordIndex.ShouldBeNull();
    }

    [Fact]
    public void Digits_only_sequence_is_EmptySequence_for_that_record()
    {
        var problem = Run(Write("digits.fasta", ">a\n 123 456\n")).Problem;

        problem.ShouldNotBeNull();
        problem.Code.ShouldBe(InputProblemCode.EmptySequence);
        problem.RecordIndex.ShouldBe(1);
    }

    [Fact]
    public void A_Latin1_file_is_an_encoding_problem_not_a_wrong_base()
    {
        var path = Write("latin1.fasta", ">a\nACGT\u00e9ACGT\n", Encoding.Latin1);

        var problem = Run(path).Problem;

        problem.ShouldNotBeNull();
        problem.Code.ShouldBe(InputProblemCode.UndecodableText);
        problem.Position.ShouldBe(5);
    }

    [Fact]
    public void A_non_ASCII_letter_is_refused_not_folded_into_a_base()
    {
        var path = Write("long_s.fasta", ">a\nACGT\u017fACGT\n");

        var problem = Run(path).Problem;

        problem.ShouldNotBeNull();
        problem.Code.ShouldBe(InputProblemCode.InvalidCharacter);
        problem.Position.ShouldBe(5);
    }

    // ---- GenBank -----------------------------------------------------------------------------

    private const string Gb =
        "LOCUS       toy    16 bp    DNA     linear   UNK 01-JAN-1980\n"
        + "FEATURES             Location/Qualifiers\n"
        + "ORIGIN\n";

    [Fact]
    public void A_GenBank_record_with_a_bad_base_reports_the_record_and_position()
    {
        var path = Write("bad.gb", Gb + "        1 acgtacgt!acgtacg\n//\n");

        var problem = Run(path).Problem;

        problem.ShouldNotBeNull();
        problem.Code.ShouldBe(InputProblemCode.InvalidCharacter);
        problem.RecordIndex.ShouldBe(1);
        problem.Position.ShouldBe(9);
    }

    [Fact]
    public void A_GenBank_record_of_only_Ns_is_skipped_like_the_worker_not_refused_under_the_error_policy()
    {
        var path = Write("ns.gb", Gb + "        1 nnnnnnnn nnnnnnnn\n//\n" + Gb.Replace("toy", "two") + "        1 acgtacgt acgtacgt\n//\n");

        var result = Run(path, policy: AmbiguityPolicy.Error);

        result.IsValid.ShouldBeTrue();
        result.RecordCount.ShouldBe(1);
    }

    [Fact]
    public void A_GenBank_record_index_counts_skipped_records()
    {
        var path = Write("idx.gb", Gb + "        1 nnnnnnnn nnnnnnnn\n//\n" + Gb.Replace("toy", "two") + "        1 acgtacgt acgtacg!\n//\n");

        var problem = Run(path).Problem;

        problem.ShouldNotBeNull();
        problem.RecordIndex.ShouldBe(2);
        problem.Position.ShouldBe(16);
    }

    // ---- privacy -----------------------------------------------------------------------------

    [Fact]
    public void A_problem_never_carries_the_file_name_or_the_header_text()
    {
        var path = Write("PatientZeroSample.fasta", ">SECRET_HEADER_TEXT\nACGTXACGTAC\n");

        var problem = Run(path).Problem;

        problem.ShouldNotBeNull();
        problem.Detail.ShouldNotContain("PatientZero", Case.Insensitive);
        problem.Detail.ShouldNotContain("SECRET_HEADER", Case.Insensitive);
        problem.Detail.Length.ShouldBeGreaterThan(0);
    }

    // ---- every code is reachable (vacuity guard for the hand-over table) -----------------------

    [Fact]
    public void Every_problem_code_except_None_is_produced_by_some_input_in_this_file()
    {
        var produced = new HashSet<InputProblemCode>
        {
            Run(Write("a.fasta", ">a\nACGTXACGT\n")).Problem!.Code,
            Run(Write("b.fasta", ">a\nACGTNACGT\n"), policy: AmbiguityPolicy.Error).Problem!.Code,
            Run(Write("c.fasta", ">a\nACGUACGT\n")).Problem!.Code,
            Run(Write("d.fasta", ">a\n 12\n")).Problem!.Code,
            Run(Write("e.fasta", "")).Problem!.Code,
            Run(Write("f.fasta", ">a\nACGT\ufffdACGT\n")).Problem!.Code,
            Run(Write("g.fasta", ">a\n" + new string('A', 50) + "\n"), limits: new InputLimits { MaxTotalLen = 40 }).Problem!.Code,
            Run(Write("h.fasta", ">a\n" + new string('A', 30) + "\n>b\n" + new string('A', 30) + "\n"), limits: new InputLimits { MaxTotalLen = 40 }).Problem!.Code,
            Run(Write("i.fasta", ">a\n" + new string('A', 30) + "\n"), limits: new InputLimits { MaxBatchNt = 20 }).Problem!.Code,
            Run(Write("j.fasta", ">a\n" + new string('A', 200) + "\n"), limits: new InputLimits { MaxFileBytes = 100 }).Problem!.Code,
            Run(Path.Combine(_dir, "missing.fasta")).Problem!.Code,
        };

        produced.ShouldBe(Enum.GetValues<InputProblemCode>().ToHashSet(), ignoreOrder: true);
    }
}
