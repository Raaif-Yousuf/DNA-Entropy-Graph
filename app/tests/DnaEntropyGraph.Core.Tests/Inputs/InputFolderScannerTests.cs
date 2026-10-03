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

    private string Touch(string relative)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "ACGT");
        return path;
    }

    [Fact]
    public void Only_files_with_a_sequence_extension_are_returned_in_name_order()
    {
        var b = Touch("b.fasta");
        var a = Touch("a.GB");
        Touch("notes.docx");
        Touch("readme.txt");

        InputFolderScanner.SequenceFiles(_dir).ShouldBe([a, b]);
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
}
