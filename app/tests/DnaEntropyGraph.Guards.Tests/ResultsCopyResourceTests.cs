using System.Xml.Linq;
using DnaEntropyGraph.Presentation.ViewModels;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>Issue #102: a Results-page key the ViewModel can ask for but the .resw lacks would show the user the key itself (Hard Rule 13).</summary>
public class ResultsCopyResourceTests
{
    [Fact]
    public void Every_results_page_key_exists_in_the_resw_is_not_empty_and_has_no_em_dash()
    {
        var resw = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in RepoPaths.AllReswFiles)
        {
            foreach (var data in XDocument.Load(file).Descendants("data"))
            {
                resw[data.Attribute("name")!.Value] = data.Element("value")?.Value ?? string.Empty;
            }
        }

        ResultsCopy.AllKeys.Count.ShouldBeGreaterThan(10);
        foreach (var key in ResultsCopy.AllKeys)
        {
            resw.ShouldContainKey(key);
            resw[key].Trim().ShouldNotBeEmpty();
            resw[key].ShouldNotContain("—", customMessage: key);
        }

        // A format string with no placeholder would print without the number it exists to show.
        foreach (var key in ResultsCopy.FormatKeys)
        {
            resw[key].ShouldContain("{0", customMessage: key);
        }
    }
}
