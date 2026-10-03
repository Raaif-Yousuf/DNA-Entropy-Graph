using System.Text;
using DnaEntropyGraph.Core.Inputs;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests.Inputs;

/// <summary>
/// Inline parity cases for shapes no worker fixture file covers (GenBank column rules, Python's
/// wider line-separator and whitespace sets, astral digits). Each verdict was MEASURED
/// 2026-10-02 by writing the exact bytes to a file and running the real worker
/// <c>dna_entropy.readers.input.load_input</c> (policy keep, worker\.venv): the expected values
/// below are what Python returned, not what C# did. To re-measure, write the bytes and run
/// load_input; do not edit an expectation to make C# pass.
/// </summary>
public sealed class InputFileValidatorMeasuredParityTests : IDisposable
{
    private const string Head =
        "LOCUS       t   16 bp    DNA     linear   UNK 01-JAN-2000\nFEATURES             Location/Qualifiers\nORIGIN\n";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "deg-measured-parity-" + Guid.NewGuid().ToString("N"));

    public InputFileValidatorMeasuredParityTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort
        }
    }

    public sealed record Case(string Id, string Ext, byte[] Data, bool Accepts, InputKind? Kind = null, int Records = 0, long Total = 0, InputProblemCode? Code = null)
    {
        public override string ToString() => Id;
    }

    private static byte[] U(string s) => new UTF8Encoding(false).GetBytes(s);

    private static Case Ok(string id, string ext, string content, InputKind kind, int records, long total) =>
        new(id, ext, U(content), true, kind, records, total);

    private static Case Refuse(string id, string ext, string content, InputProblemCode code) =>
        new(id, ext, U(content), false, Code: code);

    private static string Gb(string origin) => Head + origin + "\n//\n";

    public static IEnumerable<object[]> Cases() =>
        new[]
        {
            // GenBank ORIGIN: Biopython Scanner.parse_footer is column based.
            Ok("gb_normal", "gb", Gb("        1 acgtacgt acgtacgt"), InputKind.GenBank, 1, 16),
            Refuse("gb_nocoord", "gb", Gb("acgtacgtacgtacgt"), InputProblemCode.NoRecords),
            Ok("gb_short_line_dropped", "gb", Gb("      acg\n        1 acgtacgt acgtacgt"), InputKind.GenBank, 1, 16),
            Ok("gb_one_extra_indent_tolerated", "gb", Gb(" " + "        1 acgtacgt acgtacgt"), InputKind.GenBank, 1, 16),
            Ok("gb_line_of_10_chars_dropped", "gb", Gb("        1 \n        1 acgtacgt acgtacgt"), InputKind.GenBank, 1, 16),
            Ok("gb_line_of_11_chars_keeps_one_base", "gb", Gb("        1 a"), InputKind.GenBank, 1, 1),
            Refuse("gb_column_10_not_a_space", "gb", Gb("       10acgtacgtac"), InputProblemCode.NoRecords),
            Refuse("gb_contig_line_after_origin", "gb", Head + "        1 acgtacgt acgtacgt\nCONTIG      join(x:1..5)\n//\n", InputProblemCode.NoRecords),
            Refuse("gb_tab_in_coordinate_column", "gb", Gb("        1\tacgtacgt acgtacgt"), InputProblemCode.NoRecords),
            Ok("gb_blank_line_in_origin", "gb", Gb("        1 acgtacgt\n\n       11 acgtacgt"), InputKind.GenBank, 1, 16),
            Ok("gb_digit_inside_sequence_is_removed_by_validation", "gb", Gb("        1 acgt1cgt acgtacgt"), InputKind.GenBank, 1, 15),

            // Python str.splitlines splits on \v \f \x1c \x1d \x1e \x85 U+2028 U+2029 too.
            Ok("fa_form_feed_ends_header", "fa", ">a\fACGTACGT\n", InputKind.Fasta, 1, 8),
            Ok("fa_vertical_tab_ends_header", "fa", ">a\vACGTACGT\n", InputKind.Fasta, 1, 8),
            Ok("fa_x1c_line_between_records", "fa", ">a\n\u001c\n>b\nACGTACGT\n", InputKind.Fasta, 1, 8),
            Ok("fa_x85_ends_header", "fa", ">a\u0085ACGTACGT\n", InputKind.Fasta, 1, 8),
            Ok("fa_u2028_ends_header", "fa", ">a\u2028ACGTACGT\n", InputKind.Fasta, 1, 8),
            Ok("fa_x85_inside_sequence_line", "fa", ">a\nACGT\u0085ACGT\n", InputKind.Fasta, 1, 8),
            Ok("fa_x1c_x1d_around_sequence", "fa", ">a\n\u001cACGTACGT\u001d\n", InputKind.Fasta, 1, 8),
            Ok("fa_separator_in_header_makes_one_more_sequence_line", "fa", ">\u001ca\u001d\nACGTACGT\n", InputKind.Fasta, 1, 9),
            Ok("fa_two_records_same_id_after_nbsp", "fa", ">a\u00a0x\nACGTACGT\n>a\u00a0y\nACGTACGT\n", InputKind.Fasta, 2, 16),

            // Format sniffing on an unknown extension uses the same Python strip/splitlines.
            Ok("txt_sniff_header_ends_at_form_feed", "txt", ">h\fACGTACGT\n", InputKind.Fasta, 1, 8),
            Ok("txt_sniff_header_ends_at_x85", "txt", ">h\u0085ACGTACGT\n", InputKind.Fasta, 1, 8),
            Ok("txt_sniff_leading_x1c", "txt", "\u001c>a\nACGTACGT\n", InputKind.Fasta, 1, 8),
            Ok("txt_sniff_leading_x1c_header", "txt", "\u001c>h\nACGTACGT\n", InputKind.Fasta, 1, 8),
            Refuse("txt_sniff_locus_after_form_feed_is_genbank_without_records", "txt", "\fLOCUS x\n", InputProblemCode.NoRecords),
            Ok("txt_plain_paste", "txt", "ACGT ACGT AC\n", InputKind.Paste, 1, 10),

            // \d is Unicode Nd, including astral digits (MATHEMATICAL DOUBLE-STRUCK DIGIT ZERO).
            Ok("fa_astral_digit_removed", "fa", ">a\nAC\U0001D7CEGTACGT\n", InputKind.Fasta, 1, 8),

            // Bytes that are not UTF-8 decode to U+FFFD (three bytes, three replacements).
            new("fa_invalid_utf8_bytes", "fa", [.. U(">a\nAC"), 0xED, 0xA0, 0xBD, .. U("GTACGT\n")], false, Code: InputProblemCode.UndecodableText),
        }.Select(c => new object[] { c });

    [Fact]
    public void The_corpus_is_not_vacuous()
    {
        Cases().Count().ShouldBeGreaterThanOrEqualTo(25);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Verdict_matches_the_value_measured_from_the_worker(Case c)
    {
        var path = Path.Combine(_dir, c.Id + "." + c.Ext);
        File.WriteAllBytes(path, c.Data);

        var result = InputFileValidator.Validate(path, InputFormat.Auto, AmbiguityPolicy.Keep, treatAsRna: false);

        result.IsValid.ShouldBe(c.Accepts, $"{c.Id}: {result.Problem?.Code} {result.Problem?.Detail}");
        if (c.Accepts)
        {
            result.Kind.ShouldBe(c.Kind!.Value, c.Id);
            result.RecordCount.ShouldBe(c.Records, c.Id);
            result.TotalLength.ShouldBe(c.Total, c.Id);
        }
        else
        {
            result.Problem!.Code.ShouldBe(c.Code!.Value, c.Id);
        }
    }

    // ---- documented divergence, worker issue #492 ------------------------------------------------

    /// <summary>
    /// Worker bug #492: <c>read_genbank</c> hands back U+017F (long s) in an ORIGIN block as
    /// ASCII 'S' (Biopython upper-cases Unicode before the worker normalises), so the worker
    /// ACCEPTS a record that C# refuses. MEASURED 2026-10-02: Python load_input returned
    /// 1 contig of 16 for this file. C# stays strict; this asserts it.
    /// </summary>
    [Fact]
    public void Divergence_492_a_long_s_in_a_GenBank_origin_is_refused_here_though_the_worker_accepts_it()
    {
        var path = Path.Combine(_dir, "long_s.gb");
        File.WriteAllBytes(path, U(Gb("        1 acgtacgt\u017facgtacg")));

        var problem = InputFileValidator.Validate(path, InputFormat.Auto, AmbiguityPolicy.Keep, treatAsRna: false).Problem;

        problem.ShouldNotBeNull();
        problem.Code.ShouldBe(InputProblemCode.InvalidCharacter);
        problem.Position.ShouldBe(9);
    }

    /// <summary>
    /// A heuristic tripwire, not a proof: when #492 is fixed in the worker's reader the source
    /// will mention ASCII-only case handling. Then this goes red and the divergence above must
    /// become a normal parity case (worker accepts nothing non-ASCII).
    /// </summary>
    [Fact]
    public void Divergence_492_tripwire_goes_red_when_the_worker_reader_gains_ascii_only_handling()
    {
        var source = File.ReadAllText(Path.Combine(ContractFixtures.RepoRoot, "worker", "src", "dna_entropy", "readers", "genbank.py"));

        source.ShouldNotContain("isascii", Shouldly.Case.Insensitive, "#492 looks fixed in worker/src/dna_entropy/readers/genbank.py: turn the divergence test into a parity case");
        source.ShouldNotContain("_ASCII_UPPER", Shouldly.Case.Sensitive, "#492 looks fixed: turn the divergence test into a parity case");
    }
}
