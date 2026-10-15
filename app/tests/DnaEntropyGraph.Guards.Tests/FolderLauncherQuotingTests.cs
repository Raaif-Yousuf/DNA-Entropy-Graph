using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// Issue #642 cold review (15): <c>explorer.exe "D:\"</c> built by string concatenation ends in a backslash and a quote,
/// which the Windows command-line rules read as an escaped quote, so a drive root (or any folder with a trailing backslash)
/// opened the wrong thing. The folder goes in through <c>ProcessStartInfo.ArgumentList</c>, which quotes it correctly.
/// </summary>
public class FolderLauncherQuotingTests
{
    [Fact]
    public void OpenFolder_passes_the_path_as_an_argument_list_entry_never_a_hand_quoted_string()
    {
        var source = File.ReadAllText(Path.Combine(RepoPaths.AppRoot, "src", "DnaEntropyGraph.App", "Services", "FolderLauncher.cs"));
        var open = source[source.IndexOf("public bool OpenFolder", StringComparison.Ordinal)..source.IndexOf("public bool RevealFile", StringComparison.Ordinal)];

        open.ShouldContain("ArgumentList");
        open.ShouldNotContain("\\\"{folderPath}");
    }

    [Fact]
    public void RevealFile_builds_its_switch_through_the_tested_helper_never_a_hand_quoted_interpolation()
    {
        // Issue #677: ExplorerCommandLine.Select is what the unit tests pin (spaces, trailing backslash, quotes).
        var source = File.ReadAllText(Path.Combine(RepoPaths.AppRoot, "src", "DnaEntropyGraph.App", "Services", "FolderLauncher.cs"));
        var reveal = source[source.IndexOf("public bool RevealFile", StringComparison.Ordinal)..];

        reveal.ShouldContain("ExplorerCommandLine.Select");
        reveal.ShouldNotContain("/select,\\\"{");
    }
}
