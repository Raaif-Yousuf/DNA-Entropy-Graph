using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// Issue #677: Reset all settings refreshes every group in place (theme included), so the confirmation must not tell the user to restart.
/// The copy is a resw value, so the guard reads it where the page does.
/// </summary>
public class ResetCopyTests
{
    [Fact]
    public void The_reset_done_message_does_not_ask_for_a_restart()
    {
        var resw = File.ReadAllText(Path.Combine(RepoPaths.AppRoot, "src", "DnaEntropyGraph.App", "Strings", "en-US", "Resources.resw"));
        var start = resw.IndexOf("name=\"AdvancedResetDone\"", StringComparison.Ordinal);
        start.ShouldBeGreaterThan(0);
        var value = resw[start..resw.IndexOf("</data>", start, StringComparison.Ordinal)];

        value.ShouldNotContain("restart", Case.Insensitive);
        value.ShouldNotContain("Close the app", Case.Insensitive);
        value.ShouldNotContain("open it again", Case.Insensitive);
    }
}
