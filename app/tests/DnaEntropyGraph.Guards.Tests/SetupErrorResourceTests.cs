using System.Xml.Linq;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>Hard Rule 13 for the project-setup failures (issues #50 to #52): every code has a message, any button label, a catalog row and a triage entry.</summary>
public class SetupErrorResourceTests
{
    private static Dictionary<string, string> ReswValues()
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
    public void Every_setup_error_code_has_a_distinct_message_and_any_button_label_exists()
    {
        var resw = ReswValues();

        SetupErrorCodes.All.Count.ShouldBeGreaterThanOrEqualTo(2);
        foreach (var code in SetupErrorCodes.All)
        {
            var key = SetupErrorCodes.ResourceKey(code);
            resw.ShouldContainKey(key, $"setup error code '{code}' maps to '{key}', which Resources.resw does not define");
            resw[key].Trim().ShouldNotBeEmpty();
            resw[key].ShouldNotContain("—", customMessage: "Hard Rule 13: no em dashes in user-visible text");
            if (SetupErrorCodes.ActionResourceKey(code) is { } actionKey)
            {
                resw.ShouldContainKey(actionKey);
                resw[actionKey].Trim().ShouldNotBeEmpty();
            }
        }

        SetupErrorCodes.All.Select(SetupErrorCodes.ResourceKey).Distinct().Count().ShouldBe(SetupErrorCodes.All.Count);
        SetupErrorCodes.ResourceKey(SetupErrorCodes.ProjectQuota).ShouldBe("SetupError_PROJECT_QUOTA");
    }

    [Fact]
    public void Every_setup_error_code_is_in_the_copy_catalog_and_the_triage_roster()
    {
        var repo = Path.GetFullPath(Path.Combine(RepoPaths.AppRoot, ".."));
        var catalog = File.ReadAllText(Path.Combine(repo, "docs", "copy_catalog.md"));
        var triageText = File.ReadAllText(Path.Combine(repo, "scripts", "triage_diagnostics.py"));
        var start = triageText.IndexOf("KNOWN_CLOUD_ERROR_CODES: frozenset", StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, "the KNOWN_CLOUD_ERROR_CODES frozenset moved or was renamed");
        var triage = triageText[start..triageText.IndexOf("))", start, StringComparison.Ordinal)];

        SetupErrorCodes.All.Count.ShouldBeGreaterThanOrEqualTo(2);
        foreach (var code in SetupErrorCodes.All)
        {
            catalog.ShouldContain($"| `{code}` |", customMessage: $"docs/copy_catalog.md has no row for setup error code {code}");
            triage.ShouldContain($"\"{code}\"", customMessage: $"scripts/triage_diagnostics.py KNOWN_CLOUD_ERROR_CODES lacks {code}");
        }
    }

    [Fact]
    public void The_billing_request_text_template_exists_and_carries_both_placeholders()
    {
        var text = ReswValues()["SetupBillingRequestText"];

        text.ShouldContain("{project}");
        text.ShouldContain("{account}");
        text.ShouldNotContain("\u2014");
    }
}
