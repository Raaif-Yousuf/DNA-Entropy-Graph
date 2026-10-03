using DnaEntropyGraph.Core.Runs;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests.Runs;

/// <summary>
/// Issue #102: "Open" on a result file hands it to Windows, so only the file types the worker writes are opened.
/// The allowlist is derived from <c>worker/src/dna_entropy/writers</c> and docs/science_and_formats.md section 5.
/// </summary>
public sealed class ShellOpenPolicyTests
{
    // One real file name per extension the worker writes.
    [Theory]
    [InlineData("two.fasta")]
    [InlineData("two.gb")]
    [InlineData("two.entropy.bedgraph")]
    [InlineData("two.entropy.smooth51.bedgraph")]
    [InlineData("two.entropy.wig")]
    [InlineData("two.entropy.geneious.gff3")]
    [InlineData("two.genes.gff3")]
    [InlineData("two.regions.gff3")]
    [InlineData("two.regions.bed")]
    [InlineData("two.entropy.tsv")]
    [InlineData("two.genes.tsv")]
    [InlineData("two.genes.csv")]
    [InlineData("two.probs.tsv.gz")]
    [InlineData("two.probs.npy")]
    [InlineData("two.summary.txt")]
    [InlineData("stats.txt")]
    [InlineData("provenance.json")]
    [InlineData("TWO.FASTA")]
    [InlineData("x.exe.wig")]
    public void A_file_type_the_worker_writes_may_be_opened(string name) => ShellOpenPolicy.MayOpen(@"C:\out\run\" + name).ShouldBeTrue();

    [Theory]
    [InlineData("a.exe")]
    [InlineData("A.EXE")]
    [InlineData("x.wig.exe")]
    [InlineData("a.bat")]
    [InlineData("a.ps1")]
    [InlineData("a.lnk")]
    [InlineData("a.docm")]
    [InlineData("a.jnlp")]
    [InlineData("a.pdf")]
    [InlineData("a.zip")]
    [InlineData("noextension")]
    [InlineData("trailingdot.")]
    public void Anything_else_is_refused(string name) => ShellOpenPolicy.MayOpen(@"C:\out\run\" + name).ShouldBeFalse();

    [Fact]
    public void The_allowlist_is_not_vacuous()
    {
        ShellOpenPolicy.AllowedExtensions.Count.ShouldBeGreaterThanOrEqualTo(12);
        ShellOpenPolicy.AllowedExtensions.ShouldAllBe(e => e.StartsWith('.') && e == e.ToLowerInvariant());
    }
}
