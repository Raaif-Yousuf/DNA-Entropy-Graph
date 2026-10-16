using System.Xml.Linq;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Presets;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>Issue #136 and Hard Rule 13: the preset words live in Resources.resw, and a built-in's code name is the en-US name.</summary>
public class PresetCopyTests
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

    [Fact]
    public void Every_built_in_preset_has_a_name_equal_to_its_code_name_and_a_detail_line()
    {
        var resw = Resw();
        foreach (var preset in BuiltInPresets.All)
        {
            resw[BuiltInPresets.NameKey(preset.Id)].ShouldBe(preset.Name, "the name a run records (RunOptions.PresetName) is the code name, so the two must read the same");
            resw[BuiltInPresets.DetailKey(preset.Id)].Trim().ShouldNotBeEmpty();
        }
    }

    [Fact]
    public void Every_direction_and_topology_has_the_words_the_summary_line_uses()
    {
        var resw = Resw();
        foreach (var direction in Enum.GetValues<Direction>())
        {
            resw.ShouldContainKey("PresetDirection_" + direction);
        }

        foreach (var topology in Enum.GetValues<Topology>())
        {
            resw.ShouldContainKey("PresetTopology_" + topology);
        }

        resw["PresetSummary_Line"].ShouldContain("{3}");
    }

    [Fact]
    public void Every_preset_status_message_that_is_a_problem_names_the_button_to_choose()
    {
        var resw = Resw();
        foreach (var key in new[] { "PresetStatus_NeedFile", "PresetStatus_EmptyName", "PresetStatus_NameTaken" })
        {
            resw[key].ShouldContain(resw["NewRunPresetSave.Content"]);
        }

        resw["PresetStatus_NothingToCopy"].ShouldContain(resw["NewRunPresetCopy.Content"]);
        resw["PresetStatus_CopyFailed"].ShouldContain(resw["NewRunPresetCopy.Content"]);
        resw["PresetStatus_ImportNone"].ShouldContain(resw["NewRunPresetAdd.Content"]);
    }
}
