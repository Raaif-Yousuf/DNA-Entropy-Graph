using System.Xml.Linq;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>Hard Rule 13 for sign-in failures (issue #48): every code has a message, and a message that is not itself the action has a button label.</summary>
public class AuthErrorResourceTests
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
    public void Every_auth_error_code_has_a_distinct_message_and_any_action_label_exists()
    {
        var resw = ReswValues();

        AuthErrorCodes.All.Count.ShouldBeGreaterThanOrEqualTo(8);
        foreach (var code in AuthErrorCodes.All)
        {
            var key = AuthErrorCodes.ResourceKey(code);
            resw.ShouldContainKey(key, $"auth error code '{code}' maps to '{key}', which Resources.resw does not define");
            resw[key].Trim().ShouldNotBeEmpty();
            if (AuthErrorCodes.ActionResourceKey(code) is { } actionKey)
            {
                resw.ShouldContainKey(actionKey);
                resw[actionKey].Trim().ShouldNotBeEmpty();
            }
        }

        AuthErrorCodes.All.Select(AuthErrorCodes.ResourceKey).Distinct().Count().ShouldBe(AuthErrorCodes.All.Count);
        AuthErrorCodes.ActionResourceKey(AuthErrorCodes.SigninExpired).ShouldBe("AuthAction_SignInAgain");
    }

    [Fact]
    public void Every_auth_error_code_is_in_the_copy_catalog_and_the_triage_roster()
    {
        var repo = Path.GetFullPath(Path.Combine(RepoPaths.AppRoot, ".."));
        var catalog = File.ReadAllText(Path.Combine(repo, "docs", "copy_catalog.md"));
        var triageText = File.ReadAllText(Path.Combine(repo, "scripts", "triage_diagnostics.py"));
        var start = triageText.IndexOf("KNOWN_CLOUD_ERROR_CODES: frozenset", StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, "the KNOWN_CLOUD_ERROR_CODES frozenset moved or was renamed");
        var triage = triageText[start..triageText.IndexOf("))", start, StringComparison.Ordinal)];

        AuthErrorCodes.All.Count.ShouldBeGreaterThanOrEqualTo(11);
        foreach (var code in AuthErrorCodes.All)
        {
            catalog.ShouldContain($"| `{code}` |", customMessage: $"docs/copy_catalog.md has no row for auth error code {code}");
            triage.ShouldContain($"\"{code}\"", customMessage: $"scripts/triage_diagnostics.py KNOWN_CLOUD_ERROR_CODES lacks {code}");
        }
    }

    [Fact]
    public void The_browser_close_pages_exist()
    {
        var resw = ReswValues();

        resw["SignInBrowserSuccess"].Trim().ShouldNotBeEmpty();
        resw["SignInBrowserFailure"].Trim().ShouldNotBeEmpty();
    }
}
