using System.Text.RegularExpressions;
using System.Xml.Linq;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// Issue #264 review: the uninstall question has embedded English fallbacks (the hook may run before the resource loader works). Hard Rule 13
/// keeps Resources.resw the one source of truth, so these constants must equal the resw values exactly. Read as source text, so the guard
/// needs no reference to the WinUI project.
/// </summary>
public class UninstallStringsMatchResourcesTests
{
    private static string ReswValue(string key)
    {
        var resw = Path.Combine(RepoPaths.AppRoot, "src", "DnaEntropyGraph.App", "Strings", "en-US", "Resources.resw");
        var element = XDocument.Load(resw).Root!.Elements("data").SingleOrDefault(e => (string?)e.Attribute("name") == key);
        element.ShouldNotBeNull($"{key} is missing from Resources.resw");
        return element.Element("value")!.Value;
    }

    private static string Constant(string name)
    {
        var source = File.ReadAllText(Path.Combine(RepoPaths.AppRoot, "src", "DnaEntropyGraph.App", "Startup", "UninstallDataStrings.cs"));
        var match = Regex.Match(source, "const string " + name + " = \"(?<v>(?:[^\"\\\\]|\\\\.)*)\";");
        match.Success.ShouldBeTrue($"{name} constant not found");
        return Regex.Unescape(match.Groups["v"].Value);
    }

    [Fact]
    public void The_embedded_title_equals_the_resw_value()
        => Constant("EnglishTitle").ShouldBe(ReswValue("UninstallRemoveData_Title"));

    [Fact]
    public void The_embedded_body_equals_the_resw_value()
        => Constant("EnglishBody").ShouldBe(ReswValue("UninstallRemoveData_Body"));

    [Fact]
    public void The_keys_the_hook_asks_for_are_the_ones_in_the_resw()
    {
        Constant("TitleKey").ShouldBe("UninstallRemoveData_Title");
        Constant("BodyKey").ShouldBe("UninstallRemoveData_Body");
    }
}
