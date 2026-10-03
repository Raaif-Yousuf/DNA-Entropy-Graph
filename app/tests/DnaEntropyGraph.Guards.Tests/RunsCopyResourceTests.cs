using System.Xml.Linq;
using DnaEntropyGraph.Presentation.ViewModels;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>Issue #101: a Runs-page key the ViewModel can ask for but the .resw lacks would show the user the key itself (Hard Rule 13).</summary>
public class RunsCopyResourceTests
{
    [Fact]
    public void Every_runs_page_key_exists_in_the_resw_is_not_empty_and_has_no_em_dash()
    {
        var resw = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in RepoPaths.AllReswFiles)
        {
            foreach (var data in XDocument.Load(file).Descendants("data"))
            {
                resw[data.Attribute("name")!.Value] = data.Element("value")?.Value ?? string.Empty;
            }
        }

        RunsCopy.AllKeys.Count.ShouldBeGreaterThan(40);
        foreach (var key in RunsCopy.AllKeys)
        {
            resw.ShouldContainKey(key);
            resw[key].Trim().ShouldNotBeEmpty();
            resw[key].ShouldNotContain("\u2014", customMessage: key);
        }
    }
}
