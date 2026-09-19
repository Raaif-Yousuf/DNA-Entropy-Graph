using System.Text;
using DnaEntropyGraph.Core.Inputs;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests.Inputs;

/// <summary>
/// Port of <c>worker/tests/test_encoding.py</c>'s shape (issue #330): a UTF-8 BOM, a
/// UTF-16 BOM (either byte order, what Windows Notepad's "Unicode" save produces), and an
/// invalid byte must each be handled the same way in the app as they are in the worker.
/// </summary>
public sealed class TextDecoderTests
{
    [Fact]
    public void Plain_UTF8_bytes_decode_unchanged()
    {
        TextDecoder.DecodeBytes(Encoding.UTF8.GetBytes(">seq1\nACGT")).ShouldBe(">seq1\nACGT");
    }

    [Fact]
    public void A_UTF8_BOM_is_stripped_so_the_first_real_character_is_the_first_character()
    {
        byte[] bom = [0xEF, 0xBB, 0xBF];
        var withBom = bom.Concat(Encoding.UTF8.GetBytes(">seq1\nACGT")).ToArray();

        var decoded = TextDecoder.DecodeBytes(withBom);

        decoded.ShouldBe(">seq1\nACGT");
        decoded[0].ShouldBe('>'); // never a literal U+FEFF surviving as a real character
    }

    [Fact]
    public void A_UTF16_LE_BOM_decodes_as_UTF16_with_the_BOM_stripped()
    {
        var raw = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(">seq1\nACGT")).ToArray();

        TextDecoder.DecodeBytes(raw).ShouldBe(">seq1\nACGT");
    }

    [Fact]
    public void A_UTF16_BE_BOM_decodes_as_UTF16_with_the_BOM_stripped()
    {
        var raw = Encoding.BigEndianUnicode.GetPreamble().Concat(Encoding.BigEndianUnicode.GetBytes(">seq1\nACGT")).ToArray();

        TextDecoder.DecodeBytes(raw).ShouldBe(">seq1\nACGT");
    }

    [Fact]
    public void An_undecodable_byte_becomes_a_single_replacement_character_never_throws()
    {
        // 0xFF is never valid as a lone byte in UTF-8.
        byte[] raw = [(byte)'A', (byte)'C', 0xFF, (byte)'G', (byte)'T'];

        var decoded = TextDecoder.DecodeBytes(raw);

        decoded.ShouldContain('�');
        decoded.Length.ShouldBe(5); // A, C, replacement, G, T
    }
}
