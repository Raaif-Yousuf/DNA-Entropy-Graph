using System.Buffers.Binary;
using System.Text;
using DnaEntropyGraph.Core.Inputs;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests.Inputs;

/// <summary>
/// Issue #685 (Hard Rule 2): the app validates EMBL and SnapGene files locally with the worker's rules
/// (<c>readers/embl.py</c>, <c>readers/snapgene.py</c>, <c>readers/detect.py</c> on feat/134) before any cloud resource exists.
/// </summary>
public sealed class EmblSnapGeneInputTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "deg-embl-" + Guid.NewGuid().ToString("N"));

    public EmblSnapGeneInputTests() => Directory.CreateDirectory(_dir);

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

    // The worker's own fixture (worker/tests/data/sample.embl): two genes, 126 bp.
    private const string SampleEmbl =
        "ID   toy_locus; SV 1; linear; genomic DNA; STD; UNC; 126 BP.\n"
        + "XX\n"
        + "FH   Key             Location/Qualifiers\n"
        + "FH\n"
        + "FT   gene            1..42\n"
        + "FT                   /gene=\"geneA\"\n"
        + "FT   gene            complement(85..126)\n"
        + "FT                   /gene=\"geneB\"\n"
        + "FT   CDS             1..42\n"
        + "FT                   /gene=\"geneA\"\n"
        + "XX\n"
        + "SQ   Sequence 126 BP; 32 A; 32 C; 31 G; 31 T; 0 other;\n"
        + "     atgcgtacgt tagcactgac cggtattccg aaactggcgt aaccggttaa ccggttaacc        60\n"
        + "     ggttaaccgg ttaaccggtt aaccatgaaa cgttttggca cctaaggcct taaggcctta       120\n"
        + "     aggcct                                                                  126\n"
        + "//\n";

    private string Write(string name, string text)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }

    private string WriteBytes(string name, byte[] bytes)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static InputValidationResult Run(string path, InputFormat format = InputFormat.Auto, AmbiguityPolicy policy = AmbiguityPolicy.Keep)
        => InputFileValidator.Validate(path, format, policy, treatAsRna: false);

    // ---- SnapGene packet builder (type byte, big-endian uint32 length, data), the layout the worker walks ----

    private static byte[] Packet(byte type, byte[] data)
    {
        var bytes = new byte[5 + data.Length];
        bytes[0] = type;
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(1, 4), (uint)data.Length);
        data.CopyTo(bytes, 5);
        return bytes;
    }

    private static byte[] Cookie() => Packet(0x09, [.. "SnapGene"u8, 0, 1, 0, 15, 0, 20]);

    private static byte[] Dna(byte[] seq) => Packet(0x00, [0x01, .. seq]);

    private static byte[] Dna(string seq) => Dna(Encoding.ASCII.GetBytes(seq));

    private static byte[] Xml(string xml) => Packet(0x0A, Encoding.UTF8.GetBytes(xml));

    private static byte[] Join(params byte[][] parts) => [.. parts.SelectMany(p => p)];

    private const string TwoGeneXml =
        "<Features><Feature name=\"a\" type=\"gene\" directionality=\"1\"><Segment range=\"1-4\"/></Feature>"
        + "<Feature name=\"b\" type=\"gene\"><Segment range=\"5-8\"/></Feature>"
        + "<Feature name=\"c\" type=\"CDS\"><Segment range=\"5-8\"/></Feature></Features>";

    // ---- Detection ----

    [Theory]
    [InlineData(".embl")]
    [InlineData(".emb")]
    [InlineData(".EMBL")]
    public void An_embl_extension_is_detected_as_embl(string ext)
        => SequenceSniffer.DetectKindByExtension("x" + ext).ShouldBe(InputKind.Embl);

    [Fact]
    public void An_ID_line_sniffs_as_embl_and_a_bare_ID_word_does_not()
    {
        SequenceSniffer.DetectKindFromContent("ID   toy; SV 1; linear;\nXX\n").ShouldBe(InputKind.Embl);
        SequenceSniffer.DetectKindFromContent("IDENTITY is a word\nACGT").ShouldBe(InputKind.Paste);
    }

    [Fact]
    public void The_SnapGene_magic_bytes_decide_whatever_the_extension_says()
    {
        var path = WriteBytes("renamed.fa", Join(Cookie(), Dna("ACGTACGT")));
        SequenceSniffer.DetectKind(path).ShouldBe(InputKind.SnapGene);
    }

    [Fact]
    public void A_dna_file_that_is_plain_text_is_not_claimed_as_SnapGene()
    {
        var path = Write("plain.dna", "ACGTACGTACGT");
        SequenceSniffer.DetectKind(path).ShouldBe(InputKind.Paste);
    }

    [Fact]
    public void The_cookie_needs_the_type_byte_and_the_word_at_byte_five()
    {
        var bytes = Join(Cookie(), Dna("ACGT"));
        SequenceSniffer.IsSnapGene(bytes).ShouldBeTrue();
        bytes[0] = 0x08;
        SequenceSniffer.IsSnapGene(bytes).ShouldBeFalse();
        SequenceSniffer.IsSnapGene(bytes.AsSpan(0, 12)).ShouldBeFalse();
    }

    [Fact]
    public void The_picker_offers_embl_and_snapgene_files()
    {
        SequenceFileTypes.IsSequenceFile("a.embl").ShouldBeTrue();
        SequenceFileTypes.IsSequenceFile("a.emb").ShouldBeTrue();
        SequenceFileTypes.IsSequenceFile("a.dna").ShouldBeTrue();
    }

    // ---- EMBL validation ----

    [Fact]
    public void The_worker_sample_embl_is_valid_with_two_genes()
    {
        var result = Run(Write("sample.embl", SampleEmbl));

        result.IsValid.ShouldBeTrue(result.Problem?.Detail);
        result.Kind.ShouldBe(InputKind.Embl);
        result.RecordCount.ShouldBe(1);
        result.TotalLength.ShouldBe(126);
        result.GeneCount.ShouldBe(2);
    }

    [Fact]
    public void An_embl_file_with_an_odd_extension_is_found_by_its_ID_line()
    {
        var result = Run(Write("sample.txt", SampleEmbl));

        result.IsValid.ShouldBeTrue(result.Problem?.Detail);
        result.Kind.ShouldBe(InputKind.Embl);
    }

    [Fact]
    public void The_embl_format_option_forces_the_embl_reader()
    {
        var result = Run(Write("sample.txt", SampleEmbl), InputFormat.Embl);

        result.Kind.ShouldBe(InputKind.Embl);
        result.IsValid.ShouldBeTrue(result.Problem?.Detail);
    }

    [Fact]
    public void Two_embl_records_are_both_read_and_a_sequence_less_one_is_skipped_with_a_notice()
    {
        var text = SampleEmbl
            + "ID   empty_one; SV 1; linear; genomic DNA; STD; UNC; 0 BP.\nXX\nSQ   Sequence 0 BP;\n//\n"
            + SampleEmbl.Replace("toy_locus", "second");

        var result = Run(Write("multi.embl", text));

        result.IsValid.ShouldBeTrue(result.Problem?.Detail);
        result.RecordCount.ShouldBe(2);
        result.NoticeCodes.ShouldContain(n => n.Code == InputNoticeCode.GenBankRecordSkippedNoSequence && n.Count == 2);
    }

    [Fact]
    public void An_embl_non_ascii_letter_is_refused_with_its_record_and_position_and_never_folded_to_S()
    {
        // U+017F (long s) upper-cases to a valid S in Python and .NET alike: the worker refuses it before that happens.
        var text = SampleEmbl.Replace("atgcgtacgt tagcact", "atgcgtacgt ſagcact");

        var problem = Run(Write("lens.embl", text)).Problem;

        problem.ShouldNotBeNull();
        problem.Code.ShouldBe(InputProblemCode.InvalidCharacter);
        problem.RecordIndex.ShouldBe(1);
        problem.Position.ShouldBe(11);
    }

    [Fact]
    public void An_embl_ambiguity_code_follows_the_run_policy()
    {
        var text = SampleEmbl.Replace("atgcgtacgt tagcact", "atgcgtacgt tagcnct");

        Run(Write("n.embl", text), policy: AmbiguityPolicy.Keep).IsValid.ShouldBeTrue();
        Run(Write("n.embl", text), policy: AmbiguityPolicy.Error).Problem!.Code.ShouldBe(InputProblemCode.AmbiguityRefused);
    }

    [Fact]
    public void An_embl_digits_in_the_position_counts_are_not_sequence()
    {
        // The trailing 60/120/126 counts are digits: the validator removes them and the length stays 126.
        Run(Write("d.embl", SampleEmbl)).TotalLength.ShouldBe(126);
    }

    [Theory]
    [InlineData("ID   x; SV 1; linear; genomic DNA; STD; UNC; 0 BP.\nXX\n")] // no SQ block, no terminator
    [InlineData("")]
    [InlineData("   \n")]
    public void An_embl_file_with_no_sequence_is_NoRecords(string text)
    {
        var problem = Run(Write("none.embl", text)).Problem;

        problem.ShouldNotBeNull();
        problem.Code.ShouldBe(InputProblemCode.NoRecords);
    }

    [Fact]
    public void An_embl_file_whose_only_record_is_all_N_is_NoRecords()
    {
        var text = "ID   x; SV 1; linear; genomic DNA; STD; UNC; 8 BP.\nXX\nSQ   Sequence 8 BP;\n     nnnnnnnn        8\n//\n";

        Run(Write("n.embl", text)).Problem!.Code.ShouldBe(InputProblemCode.NoRecords);
    }

    [Fact]
    public void An_embl_record_over_the_length_cap_is_refused()
    {
        var result = InputFileValidator.Validate(Write("s.embl", SampleEmbl), InputFormat.Auto, AmbiguityPolicy.Keep, false, new InputLimits { MaxTotalLen = 100 });

        result.Problem!.Code.ShouldBe(InputProblemCode.RecordTooLong);
    }

    // ---- SnapGene validation ----

    [Fact]
    public void A_snapgene_file_is_valid_and_counts_its_gene_features()
    {
        var path = WriteBytes("plasmid.dna", Join(Cookie(), Dna("ACGTACGTACGTACGT"), Xml(TwoGeneXml)));

        var result = Run(path);

        result.IsValid.ShouldBeTrue(result.Problem?.Detail);
        result.Kind.ShouldBe(InputKind.SnapGene);
        result.RecordCount.ShouldBe(1);
        result.TotalLength.ShouldBe(16);
        result.GeneCount.ShouldBe(2);
    }

    [Fact]
    public void The_snapgene_format_option_forces_the_reader_and_a_text_file_is_then_refused()
    {
        var result = Run(Write("not.dna", "ACGTACGTACGT"), InputFormat.SnapGene);

        result.Kind.ShouldBe(InputKind.SnapGene);
        result.Problem!.Code.ShouldBe(InputProblemCode.NoRecords);
    }

    [Fact]
    public void A_snapgene_non_ascii_dna_byte_is_refused_with_its_position()
    {
        var path = WriteBytes("bad.dna", Join(Cookie(), Dna([.. "ACG"u8, 0xC5, .. "TAC"u8])));

        var problem = Run(path).Problem;

        problem.ShouldNotBeNull();
        problem.Code.ShouldBe(InputProblemCode.InvalidCharacter);
        problem.Position.ShouldBe(4);
    }

    [Fact]
    public void A_truncated_snapgene_packet_is_refused()
    {
        var whole = Join(Cookie(), Dna("ACGTACGTACGTACGT"));

        Run(WriteBytes("cut.dna", whole[..^4])).Problem!.Code.ShouldBe(InputProblemCode.NoRecords);
    }

    [Fact]
    public void A_snapgene_file_with_no_dna_packet_is_refused()
    {
        Run(WriteBytes("nodna.dna", Join(Cookie(), Xml(TwoGeneXml)))).Problem!.Code.ShouldBe(InputProblemCode.NoRecords);
    }

    [Theory]
    [InlineData("<!DOCTYPE x [<!ENTITY a \"b\">]><Features/>")]
    [InlineData("<Features><!  entity a 'b'></Features>")]
    public void A_snapgene_xml_packet_with_a_DOCTYPE_or_ENTITY_is_refused(string xml)
    {
        var path = WriteBytes("xxe.dna", Join(Cookie(), Dna("ACGTACGT"), Xml(xml)));

        Run(path).Problem!.Code.ShouldBe(InputProblemCode.NoRecords);
    }

    [Fact]
    public void A_snapgene_xml_packet_over_8_MiB_is_refused()
    {
        var path = WriteBytes("big.dna", Join(Cookie(), Dna("ACGTACGT"), Packet(0x0A, new byte[(8 * 1024 * 1024) + 1])));

        Run(path).Problem!.Code.ShouldBe(InputProblemCode.NoRecords);
    }

    [Fact]
    public void A_snapgene_file_over_the_file_cap_is_refused_before_it_is_read()
    {
        var path = WriteBytes("cap.dna", Join(Cookie(), Dna("ACGTACGT")));

        var result = InputFileValidator.Validate(path, InputFormat.Auto, AmbiguityPolicy.Keep, false, new InputLimits { MaxFileBytes = 10 });

        result.Problem!.Code.ShouldBe(InputProblemCode.FileTooLarge);
    }

    [Fact]
    public void Snapgene_ambiguity_and_length_rules_run_through_the_shared_validator()
    {
        var path = WriteBytes("n.dna", Join(Cookie(), Dna("ACGTNACGT")));

        Run(path, policy: AmbiguityPolicy.Error).Problem!.Code.ShouldBe(InputProblemCode.AmbiguityRefused);
    }

    [Fact]
    public void Embl_and_snapgene_inputs_offer_the_annotated_output_group()
    {
        OutputCatalog.GroupFor(InputKind.Embl).ShouldBe(OutputInputKinds.GenBank);
        OutputCatalog.GroupFor(InputKind.SnapGene).ShouldBe(OutputInputKinds.GenBank);
        OutputCatalog.GroupFor(InputKind.Fasta).ShouldBe(OutputInputKinds.Sequence);
    }
}
