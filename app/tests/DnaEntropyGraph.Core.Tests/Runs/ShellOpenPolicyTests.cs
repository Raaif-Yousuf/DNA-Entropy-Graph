using DnaEntropyGraph.Core.Runs;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests.Runs;

/// <summary>Issue #102: "Open" on a result file hands it to Windows, so a file that would RUN rather than open is refused.</summary>
public sealed class ShellOpenPolicyTests
{
    [Theory]
    [InlineData(@"C:\out\run\a.gb")]
    [InlineData(@"C:\out\run\a.fasta")]
    [InlineData(@"C:\out\run\a.entropy.bedgraph")]
    [InlineData(@"C:\out\run\a.summary.txt")]
    [InlineData(@"C:\out\run\provenance.json")]
    [InlineData(@"C:\out\run\noextension")]
    public void A_data_file_may_be_opened(string path) => ShellOpenPolicy.MayOpen(path).ShouldBeTrue();

    [Theory]
    [InlineData(@"C:\out\run\a.exe")]
    [InlineData(@"C:\out\run\A.EXE")]
    [InlineData(@"C:\out\run\a.bat")]
    [InlineData(@"C:\out\run\a.cmd")]
    [InlineData(@"C:\out\run\a.ps1")]
    [InlineData(@"C:\out\run\a.vbs")]
    [InlineData(@"C:\out\run\a.js")]
    [InlineData(@"C:\out\run\a.msi")]
    [InlineData(@"C:\out\run\a.scr")]
    [InlineData(@"C:\out\run\a.lnk")]
    [InlineData(@"C:\out\run\a.url")]
    [InlineData(@"C:\out\run\a.hta")]
    [InlineData(@"C:\out\run\a.com")]
    public void A_file_that_would_run_is_refused(string path) => ShellOpenPolicy.MayOpen(path).ShouldBeFalse();
}
