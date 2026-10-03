using DnaEntropyGraph.Core.Inputs;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests.Inputs;

/// <summary>Issue #63: a dropped folder contributes the sequence files directly inside it.</summary>
public sealed class InputFolderScannerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "deg-folder-" + Guid.NewGuid().ToString("N"));

    public InputFolderScannerTests() => Directory.CreateDirectory(_dir);

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

    private string Touch(string relative, string content = "ACGT")
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Only_files_the_Add_files_picker_offers_are_returned_in_name_order()
    {
        var b = Touch("b.fasta");
        var a = Touch("a.GB");
        var c = Touch("plain.txt", ">plain\nACGT\n");
        Touch("notes.docx");

        InputFolderScanner.SequenceFiles(_dir).ShouldBe([a, b, c]);
    }

    [Fact]
    public void Files_in_a_sub_folder_are_not_followed()
    {
        var top = Touch("top.fa");
        Touch(Path.Combine("sub", "deep.fa"));

        InputFolderScanner.SequenceFiles(_dir).ShouldBe([top]);
    }

    [Fact]
    public void An_empty_folder_gives_an_empty_list()
        => InputFolderScanner.SequenceFiles(_dir).ShouldBeEmpty();

    [Fact]
    public void A_folder_that_does_not_exist_gives_an_empty_list_not_an_exception()
        => InputFolderScanner.SequenceFiles(Path.Combine(_dir, "missing")).ShouldBeEmpty();

    // DECISION (agent-made, reversible): a .txt in a dropped folder is taken only when its first line is a FASTA or GenBank header,
    // so a folder of notes and logs does not become a pile of error pills. A headerless sequence file is still added with Add files.
    [Fact]
    public void A_txt_file_is_taken_only_when_it_starts_with_a_FASTA_or_GenBank_header()
    {
        var fasta = Touch("a.txt", "\n>a\nACGT\n");
        var genbank = Touch("b.txt", "LOCUS       T1   4 bp\n");
        Touch("notes.txt", "Meeting notes\nbuy primers\n");
        Touch("bare.txt", "ACGTACGT\n");
        Touch("empty.txt", string.Empty);
        var withBom = Path.Combine(_dir, "c.txt");
        File.WriteAllBytes(withBom, [0xEF, 0xBB, 0xBF, .. "> c\nACGT\n"u8.ToArray()]);

        InputFolderScanner.SequenceFiles(_dir).ShouldBe([fasta, genbank, withBom]);
    }

    [Fact]
    public void A_file_with_a_sequence_extension_is_taken_without_looking_inside()
    {
        var fa = Touch("odd.fa", "not a header at all");

        InputFolderScanner.SequenceFiles(_dir).ShouldBe([fa]);
    }
}
