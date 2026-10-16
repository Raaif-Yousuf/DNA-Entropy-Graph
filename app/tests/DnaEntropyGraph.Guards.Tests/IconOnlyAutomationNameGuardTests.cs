using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// Issue #110: a button that shows only an icon has no text for Narrator to read, so it needs an
/// <c>AutomationProperties.Name</c> (an attribute, or an <c>x:Uid</c> whose .resw has
/// <c>&lt;Uid&gt;.AutomationProperties.Name</c>). A missing name compiles, looks right and is read as just "button".
/// </summary>
public sealed class IconOnlyAutomationNameGuardTests
{
    private static readonly Regex ButtonElement = new(
        @"<(?<tag>Button|AppBarButton|AppBarToggleButton|ToggleButton|HyperlinkButton|RepeatButton)\b(?<attrs>[^>]*?)(?:/>|>(?<body>.*?)</\k<tag>>)",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex UidAttr = new(@"x:Uid=""(?<uid>[^""]+)""", RegexOptions.Compiled);

    private static readonly Regex IconInBody = new(@"<(FontIcon|SymbolIcon|PathIcon|BitmapIcon|ImageIcon|Image|AnimatedIcon)\b", RegexOptions.Compiled);

    internal sealed record Finding(string File, string Snippet);

    internal static IReadOnlyList<Finding> Scan(string file, string xaml, ISet<string> reswNames)
    {
        var findings = new List<Finding>();
        foreach (Match match in ButtonElement.Matches(xaml))
        {
            var attrs = match.Groups["attrs"].Value;
            var body = match.Groups["body"].Value;
            var hasIcon = Regex.IsMatch(attrs, @"\bIcon=") || IconInBody.IsMatch(body);
            if (!hasIcon)
            {
                continue;
            }

            var uid = UidAttr.Match(attrs) is { Success: true } u ? u.Groups["uid"].Value : null;
            var hasText = Regex.IsMatch(attrs, @"\bContent=") || Regex.IsMatch(attrs, @"\bLabel=")
                || (uid is not null && (reswNames.Contains(uid + ".Content") || reswNames.Contains(uid + ".Label")))
                || Regex.IsMatch(body, @"<TextBlock\b");
            if (hasText)
            {
                continue;
            }

            var named = Regex.IsMatch(attrs, @"AutomationProperties\.Name=")
                || (uid is not null && reswNames.Contains(uid + ".AutomationProperties.Name"));
            if (!named)
            {
                findings.Add(new Finding(file, match.Value.Length > 120 ? match.Value[..120] : match.Value));
            }
        }

        return findings;
    }

    private static HashSet<string> ReswNames()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var resw in RepoPaths.AllReswFiles)
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(resw), @"<data name=""(?<n>[^""]+)"""))
            {
                names.Add(m.Groups["n"].Value);
            }
        }

        return names;
    }

    [Fact]
    public void Every_icon_only_button_in_the_real_XAML_has_an_AutomationProperties_Name()
    {
        var files = RepoPaths.AllXamlFiles;
        files.Length.ShouldBeGreaterThanOrEqualTo(2);
        var resw = ReswNames();
        resw.Count.ShouldBeGreaterThan(50, "the guard must have read the real .resw, or an x:Uid name could never be found.");

        var findings = files.SelectMany(f => Scan(f, File.ReadAllText(f), resw)).ToList();

        findings.ShouldBeEmpty("an icon-only button needs AutomationProperties.Name so a screen reader can name it (issue #110).");
    }

    [Fact]
    public void A_planted_icon_only_button_without_a_name_is_flagged()
    {
        var xaml = "<AppBarButton Icon=\"Add\" />\n<Button><FontIcon Glyph=\"&#xE710;\" /></Button>";

        Scan("planted.xaml", xaml, new HashSet<string>()).Count.ShouldBe(2);
    }

    [Fact]
    public void A_named_or_text_button_is_not_flagged()
    {
        var xaml = """
            <Button AutomationProperties.Name="Add"><FontIcon Glyph="x" /></Button>
            <Button x:Uid="AddIcon"><FontIcon Glyph="x" /></Button>
            <Button Content="Add"><FontIcon Glyph="x" /></Button>
            <Button x:Uid="Plain" />
            """;

        Scan("ok.xaml", xaml, new HashSet<string> { "AddIcon.AutomationProperties.Name" }).ShouldBeEmpty();
    }
}
