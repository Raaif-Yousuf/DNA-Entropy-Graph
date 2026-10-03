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
    public void The_browser_close_pages_exist()
    {
        var resw = ReswValues();

        resw["SignInBrowserSuccess"].Trim().ShouldNotBeEmpty();
        resw["SignInBrowserFailure"].Trim().ShouldNotBeEmpty();
    }
}
