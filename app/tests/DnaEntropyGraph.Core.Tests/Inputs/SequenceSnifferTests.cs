using DnaEntropyGraph.Core.Inputs;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests.Inputs;

/// <summary>
/// Port of <c>worker/src/dna_entropy/readers/detect.py</c>'s rules: extension first, then
/// a content sniff of the first non-blank line, and null (paste) always wins outright.
/// </summary>
public sealed class SequenceSnifferTests
{
    [Theory]
    [InlineData(".gb")]
    [InlineData(".gbk")]
    [InlineData(".genbank")]
    [InlineData(".gbff")]
    [InlineData(".GB")] // extension match is case-insensitive
    public void A_recognised_genbank_extension_wins_without_reading_content(string ext)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ext);
        try
        {
            File.WriteAllText(path, ">not actually genbank content"); // extension wins regardless of content
            SequenceSniffer.DetectKind(path).ShouldBe(InputKind.GenBank);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(".fa")]
    [InlineData(".fasta")]
    [InlineData(".fna")]
    [InlineData(".ffn")]
    public void A_recognised_fasta_extension_wins(string ext)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ext);
        try
        {
            File.WriteAllText(path, "LOCUS not actually genbank"); // extension wins regardless of content
            SequenceSniffer.DetectKind(path).ShouldBe(InputKind.Fasta);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void An_unknown_extension_sniffs_LOCUS_as_genbank()
    {
        SequenceSniffer.DetectKindFromContent("LOCUS       toy_locus  126 bp\n...").ShouldBe(InputKind.GenBank);
    }

    [Fact]
    public void An_unknown_extension_sniffs_a_leading_angle_bracket_as_fasta()
    {
        SequenceSniffer.DetectKindFromContent(">seq1 description\nACGT").ShouldBe(InputKind.Fasta);
    }

    [Fact]
    public void Content_that_is_neither_is_paste()
    {
        SequenceSniffer.DetectKindFromContent("ACGTACGTACGT").ShouldBe(InputKind.Paste);
    }

    [Fact]
    public void Leading_blank_lines_are_skipped_before_the_sniff()
    {
        SequenceSniffer.DetectKindFromContent("\n\n   \n>seq1\nACGT").ShouldBe(InputKind.Fasta);
    }

    [Fact]
    public void Null_path_is_always_paste()
    {
        SequenceSniffer.DetectKind(null).ShouldBe(InputKind.Paste);
    }

    [Fact]
    public void A_missing_file_with_an_unrecognised_extension_falls_back_to_paste_rather_than_throwing()
    {
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xyz_does_not_exist");
        SequenceSniffer.DetectKind(missing).ShouldBe(InputKind.Paste);
    }
}
