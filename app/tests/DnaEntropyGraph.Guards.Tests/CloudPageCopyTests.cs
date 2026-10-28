using System.Text.RegularExpressions;
using System.Xml.Linq;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>Hard Rule 13 for the Cloud page (issue #105, cold review #740 finding 8): units and states in plain words, from the string table, one action per message.</summary>
public class CloudPageCopyTests
{
    private static Dictionary<string, string> Resw()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in RepoPaths.AllReswFiles)
        {
            foreach (var data in XDocument.Load(file).Descendants("data"))
            {
                values[data.Attribute("name")!.Value] = data.Element("value")?.Value ?? string.Empty;
            }
        }

        return values;
    }

    [Theory]
    [InlineData("CloudDisk_Attached")]
    [InlineData("CloudDisk_Detached")]
    public void A_disk_line_shows_its_size_and_whether_it_is_in_use_and_never_Googles_raw_status_word(string key)
    {
        var text = Resw()[key];

        text.ShouldContain("{0}");
        text.ShouldNotContain("{1}", customMessage: "the second argument was Google's raw status (READY, CREATING); a lab user cannot act on it");
        text.ShouldNotContain("READY");
        text.ShouldNotContain("CREATING");
    }

    [Fact]
    public void A_computer_from_another_copy_of_the_app_names_exactly_one_action()
    {
        var text = Resw()["CloudVm_AnotherComputer"];

        text.ShouldNotContain(" or ", customMessage: "two ways out is two actions (Hard Rule 13)");
        text.ShouldNotContain("Console", customMessage: "Cloud Console is the second action this message used to name");
        text.ShouldContain("use the copy of the app that created it");
    }

    [Theory]
    [InlineData("CloudSize_GB", "GB")]
    [InlineData("CloudSize_MB", "MB")]
    [InlineData("CloudSize_KB", "KB")]
    [InlineData("CloudSize_B", "bytes")]
    public void Every_size_unit_the_page_prints_has_a_string_with_one_number_placeholder(string key, string unit)
    {
        var text = Resw()[key];

        text.ShouldBe("{0} " + unit);
    }

    [Fact]
    public void The_cloud_page_view_model_writes_no_unit_literal_of_its_own()
    {
        var source = File.ReadAllText(Path.Combine(RepoPaths.AppRoot, "src", "DnaEntropyGraph.Presentation", "ViewModels", "CloudResourcesViewModel.cs"));

        source.Length.ShouldBeGreaterThan(1000, "the scan must have read the real file");
        Regex.IsMatch(source, "\"\\s*(GB|MB|KB|B|bytes)\"").ShouldBeFalse("a unit word typed in code skips Resources.resw (Hard Rule 13)");
    }
}
