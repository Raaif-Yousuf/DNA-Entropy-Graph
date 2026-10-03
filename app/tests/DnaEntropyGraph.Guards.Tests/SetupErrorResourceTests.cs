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

        // null is the catch-all: any code not in All (Google's own status name) shows SetupError_OTHER, so it is checked too.
        foreach (var code in SetupErrorCodes.All.Cast<string?>().Append(null))
        {
            var key = SetupErrorCodes.ResourceKey(code);
            resw.ShouldContainKey(key, $"setup error code '{code}' maps to '{key}', which Resources.resw does not define");
            resw[key].Trim().ShouldNotBeEmpty();
            resw[key].ShouldNotContain("—", customMessage: "Hard Rule 13: no em dashes in user-visible text");
            resw[key].ShouldNotContain("–", customMessage: "Hard Rule 13: no en dashes in user-visible text");

            // A message that says "choose X" must be backed by an action key that exists (the catch-all included).
            var actionKey = SetupErrorCodes.ActionResourceKey(code);
            actionKey.ShouldNotBeNull($"setup error '{key}' names an action, so it needs a SetupAction_ key");
            resw.ShouldContainKey(actionKey);
            resw[actionKey].Trim().ShouldNotBeEmpty();
            resw[actionKey].ShouldNotContain("—");
            resw[actionKey].ShouldNotContain("–");
            resw[key].ShouldContain(resw[actionKey], customMessage: $"'{key}' must name its button '{resw[actionKey]}' by its label");
        }

        SetupErrorCodes.All.Select(SetupErrorCodes.ResourceKey).Distinct().Count().ShouldBe(SetupErrorCodes.All.Count);
        SetupErrorCodes.ResourceKey(SetupErrorCodes.ProjectQuota).ShouldBe("SetupError_PROJECT_QUOTA");
    }

    [Theory]
    [InlineData(SetupErrorCodes.Permission, "SetupError_PERMISSION", "SetupAction_CopyRequestForOwner")]
    [InlineData(SetupErrorCodes.ApiDisabled, "SetupError_API_DISABLED", "SetupAction_TurnItOn")]
    [InlineData(SetupErrorCodes.NoBilling, "SetupError_NO_BILLING", "SetupAction_LinkBilling")]
    [InlineData(SetupErrorCodes.BillingAccountOff, "SetupError_BILLING_ACCOUNT_OFF", "SetupAction_FixBillingAccount")]
    public void A_permanent_failure_never_shows_the_try_again_catch_all(string code, string messageKey, string actionKey)
    {
        // Retrying a permission, switched-off service or missing billing error can never work, so none may fall to the
        // catch-all whose action is Try again.
        SetupErrorCodes.All.ShouldContain(code);
        SetupErrorCodes.ResourceKey(code).ShouldBe(messageKey);
        SetupErrorCodes.ActionResourceKey(code).ShouldBe(actionKey);
        SetupErrorCodes.ActionResourceKey(code).ShouldNotBe(SetupErrorCodes.ActionResourceKey(null));
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

    [Theory]
    [InlineData("BUCKET_CONFIG_NOT_APPLIED", "SetupError_BUCKET_CONFIG_NOT_APPLIED")]
    [InlineData("BUCKET_NAME_TAKEN", "SetupError_BUCKET_NAME_TAKEN")]
    public void The_results_bucket_failures_are_in_the_setup_roster_and_do_not_fall_to_the_catch_all(string code, string messageKey)
    {
        SetupErrorCodes.All.ShouldContain(code);
        SetupErrorCodes.ResourceKey(code).ShouldBe(messageKey);
        ReswValues().ShouldContainKey(messageKey);
    }

    [Theory]
    [InlineData(SetupErrorCodes.BucketConfigNotApplied)]
    [InlineData(SetupErrorCodes.BucketNameTaken)]
    public void The_copy_catalog_body_of_a_bucket_code_is_the_resw_text_exactly(string code)
    {
        var repo = Path.GetFullPath(Path.Combine(RepoPaths.AppRoot, ".."));
        var catalog = File.ReadAllText(Path.Combine(repo, "docs", "copy_catalog.md"));
        var row = catalog.Split('\n').Single(l => l.StartsWith($"| `{code}` |", StringComparison.Ordinal));

        row.ShouldContain(ReswValues()[SetupErrorCodes.ResourceKey(code)]);
    }
}