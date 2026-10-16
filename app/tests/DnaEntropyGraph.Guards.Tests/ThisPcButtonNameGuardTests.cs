using System.Xml.Linq;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// A string that tells the user to "choose" a button must name the button's real label. The This PC refresh button reads "Check again";
/// four strings once told the user to choose "Refresh", which is not on that page (review of #120/#173, 2026-10-15).
/// </summary>
public class ThisPcButtonNameGuardTests
{
    private static Dictionary<string, string> Entries()
    {
        var all = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in RepoPaths.AllReswFiles)
        {
            foreach (var data in XDocument.Load(file).Root!.Elements("data"))
            {
                all[data.Attribute("name")!.Value] = data.Element("value")?.Value ?? string.Empty;
            }
        }

        return all;
    }

    [Fact]
    public void The_button_is_called_Check_again_and_no_This_PC_string_sends_the_user_to_a_Refresh_button()
    {
        var entries = Entries();

        entries["ThisPcRefreshButton.Content"].ShouldBe("Check again");
        var scanned = entries.Where(e => e.Key.StartsWith("ThisPc", StringComparison.Ordinal) || e.Key.StartsWith("RunTarget", StringComparison.Ordinal) || e.Key.StartsWith("RunError_local", StringComparison.Ordinal)).ToList();
        scanned.Count.ShouldBeGreaterThan(30, "the scan must have read the This PC copy");
        scanned.Where(e => e.Value.Contains("Refresh", StringComparison.Ordinal)).Select(e => e.Key).ShouldBeEmpty();
    }
}
