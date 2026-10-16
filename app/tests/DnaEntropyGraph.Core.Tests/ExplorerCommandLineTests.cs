using DnaEntropyGraph.Core;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests;

/// <summary>
/// Issue #677: Reveal in Explorer builds <c>/select,"path"</c>. Explorer does not read that switch with the usual command-line rules, so the quoting is one tested function:
/// a path it cannot quote safely (a quote character, a trailing backslash, no drive or share) is refused rather than sent to Explorer to open the wrong thing.
/// </summary>
public class ExplorerCommandLineTests
{
    [Theory]
    [InlineData(@"C:\Lab\a.bedgraph", "/select,\"C:\\Lab\\a.bedgraph\"")]
    [InlineData(@"C:\Lab Data\my run\a b.bedgraph", "/select,\"C:\\Lab Data\\my run\\a b.bedgraph\"")]
    [InlineData(@"\\server\share\r&d\a.bed", "/select,\"\\\\server\\share\\r&d\\a.bed\"")]
    public void A_rooted_file_path_is_quoted_whole_after_the_select_switch(string path, string expected)
        => ExplorerCommandLine.Select(path).ShouldBe(expected);

    [Theory]
    [InlineData(@"C:\Lab\")]
    [InlineData(@"C:\Lab\sub\\")]
    [InlineData("C:\\Lab\\a\"b.bed")]
    [InlineData(@"relative\a.bed")]
    [InlineData(@"C:a.bed")] // drive-relative: rooted, but resolved against that drive's current directory
    [InlineData(@"C:Lab\a.bed")]
    [InlineData(@"\Lab\a.bed")] // root-relative: rooted, but on whichever drive Explorer is on
    [InlineData(@"\a.bed")]
    [InlineData("")]
    [InlineData("   ")]
    public void A_path_that_cannot_be_quoted_safely_is_refused(string path)
        => ExplorerCommandLine.Select(path).ShouldBeNull();
}
