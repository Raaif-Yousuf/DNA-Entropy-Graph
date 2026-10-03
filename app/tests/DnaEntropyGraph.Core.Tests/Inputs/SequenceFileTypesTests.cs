using DnaEntropyGraph.Core.Inputs;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests.Inputs;

/// <summary>Review of #63: the Add files picker and a dropped folder use one list of extensions.</summary>
public sealed class SequenceFileTypesTests
{
    [Fact]
    public void The_list_holds_every_FASTA_and_GenBank_extension_the_sniffer_knows_and_plain_text()
    {
        SequenceFileTypes.Extensions.ShouldContain(".fasta");
        SequenceFileTypes.Extensions.ShouldContain(".gbff");
        SequenceFileTypes.Extensions.ShouldContain(".txt");
        SequenceFileTypes.Extensions.Count.ShouldBe(SequenceFileTypes.Extensions.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Theory]
    [InlineData(@"C:\x\a.FA", true)]
    [InlineData(@"C:\x\a.txt", true)]
    [InlineData(@"C:\x\a.docx", false)]
    [InlineData(@"C:\x\a", false)]
    public void IsSequenceFile_follows_the_list(string path, bool expected)
        => SequenceFileTypes.IsSequenceFile(path).ShouldBe(expected);

    [Fact]
    public void Every_extension_the_sniffer_recognises_is_in_the_list()
    {
        foreach (var extension in SequenceSniffer.KnownExtensions)
        {
            SequenceFileTypes.Extensions.ShouldContain(extension);
        }

        SequenceSniffer.KnownExtensions.ShouldNotBeEmpty();
    }
}
