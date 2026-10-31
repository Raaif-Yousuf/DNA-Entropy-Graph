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

    /// <summary>Issue #922: the picture goes in <c>&lt;run&gt; exports &lt;code&gt;</c>, not in a folder "ending in exports".</summary>
    [Fact]
    public void The_save_picture_failure_text_names_the_folder_the_picture_really_goes_in()
    {
        var value = RepoPaths.AllReswFiles
            .SelectMany(f => XDocument.Load(f).Descendants("data"))
            .Single(d => d.Attribute("name")!.Value == ResultsCopy.ExportPngFailedInExports).Element("value")!.Value;

        value.ShouldNotContain("ending in");
        value.ShouldContain("named after the run followed by \"exports\" and a short code");
    }
}
