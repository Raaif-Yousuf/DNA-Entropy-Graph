using System.Text.RegularExpressions;
using DnaEntropyGraph.Core.Palettes;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// Issue #195: every track colour is defined once, in <c>Core/Palettes/TrackPalette.cs</c>. The consumers (overview chart, Runs sparkline,
/// PNG export, igv.js load plan and bridge) take it from there; this fails when one of them spells a colour itself, and when one stops reading
/// the palette provider. The viewer's two backgrounds are pinned to <see cref="PaletteBackgrounds"/>, the backgrounds the palettes are validated on.
/// </summary>
public class TrackPaletteSingleSourceGuardTests
{
    // Every source file the user can see colors from: the App and Presentation projects. The palette definition is in Core, so it is not scanned.
    private static readonly string[] Roots = ["DnaEntropyGraph.App", "DnaEntropyGraph.Presentation"];
    private static readonly string[] Extensions = [".cs", ".xaml", ".js", ".css"];

    // Files that may spell a color, each with the reason. Add here AND explain in docs/ui_conventions.md (Track colours), or add to neither.
    // MEASURED 2026-10-31 on the tree at f75845b3: the glob flagged exactly these (the first three are not track colors); nothing else.
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Services/CaptionButtonPalette.cs"] = "the title-bar caption button colors, drawn by the system in the system theme, not a track (ui_conventions.md, CaptionButtonPalette)",
        ["Assets/viewer/viewer.css"] = "the viewer page background and text; pinned to PaletteBackgrounds.Light by The_viewer_backgrounds_are_the_ones_the_palettes_are_validated_on",
        ["Assets/viewer/dark.css"] = "the viewer dark page background and text; pinned to PaletteBackgrounds.Dark by the same test",
        ["Assets/viewer/igv.min.js"] = "the vendored igv.js bundle (MIT, igv.LICENSE.txt); it is not our code and holds its own defaults",
    };

    // A hex color, an rgb()/rgba() call, a literal-argument color constructor or factory (a computed one, like FromArgb(255, r, g, b) with variables, is
    // a conversion of a palette hex and is fine), or a named color class member other than the neutral paper and ink ones. `\bColors\.` covers
    // ScottPlot.Colors.X, Microsoft.UI.Colors.X (fully qualified or not) and Windows.UI.Colors.X.
    private static readonly Regex Literal = new(
        @"#(?:[0-9A-Fa-f]{8}|[0-9A-Fa-f]{6})\b"
        + @"|\brgba?\("
        + @"|\bnew\s+(?:\w+\.)*Color\s*\("
        + @"|\bColor(?:Helper)?\.FromHex\(\s*"""
        + @"|\bColor(?:Helper)?\.From(?:Rgb|RGB|Argb|ARGB|Rgba|RGBA)\(\s*(?:0x[0-9A-Fa-f]+|\d+)\s*,\s*(?:0x[0-9A-Fa-f]+|\d+)\s*,\s*(?:0x[0-9A-Fa-f]+|\d+)\s*(?:,\s*(?:0x[0-9A-Fa-f]+|\d+)\s*)?\)"
        + @"|\bColors\.(?!Transparent\b|White\b|Black\b)\w+",
        RegexOptions.CultureInvariant);

    /// <summary>The source with comments blanked out (line breaks kept, so line numbers hold). String and char literals are left alone, since a color is spelled in one; a `//` inside a string (a URL) is not a comment.</summary>
    internal static string StripComments(string source, string extension)
    {
        var slashComments = extension is ".cs" or ".js";
        var starComments = extension is ".cs" or ".js" or ".css";
        var markupComments = extension is ".xaml";
        var o = new System.Text.StringBuilder(source.Length);
        char quote = '\0';
        for (var i = 0; i < source.Length; i++)
        {
            var ch = source[i];
            var next = i + 1 < source.Length ? source[i + 1] : '\0';
            if (quote != '\0')
            {
                o.Append(ch);
                if (ch == '\\' && i + 1 < source.Length && extension != ".css")
                {
                    o.Append(source[++i]);
                }
                else if (ch == quote || ch == '\n')
                {
                    quote = '\0';
                }

                continue;
            }

            if (slashComments && ch == '/' && next == '/')
            {
                while (i < source.Length && source[i] != '\n')
                {
                    i++;
                }

                if (i < source.Length)
                {
                    o.Append('\n');
                }

                continue;
            }

            if (starComments && ch == '/' && next == '*')
            {
                i += 2;
                while (i < source.Length && !(source[i] == '*' && i + 1 < source.Length && source[i + 1] == '/'))
                {
                    o.Append(source[i] == '\n' ? '\n' : ' ');
                    i++;
                }

                i++;
                continue;
            }

            if (markupComments && string.CompareOrdinal(source, i, "<!--", 0, 4) == 0)
            {
                i += 4;
                while (i < source.Length && string.CompareOrdinal(source, i, "-->", 0, 3) != 0)
                {
                    o.Append(source[i] == '\n' ? '\n' : ' ');
                    i++;
                }

                i += 2;
                continue;
            }

            if (ch is '"' or '\'' or '`')
            {
                quote = ch;
            }

            o.Append(ch);
        }

        return o.ToString();
    }

    internal static IReadOnlyList<string> Literals(string source, string extension = ".cs")
        => [.. StripComments(source, extension).Split('\n').Select((line, i) => (line, n: i + 1)).Where(x => Literal.IsMatch(x.line)).Select(x => $"line {x.n}: {x.line.Trim()}")];

    private static string AppFile(string relative)
    {
        var path = Path.Combine(RepoPaths.AppRoot, relative);
        File.Exists(path).ShouldBeTrue($"the guard must find {relative}, or it checks nothing.");
        return path;
    }

    private static string Read(string relative) => File.ReadAllText(AppFile(relative));

    /// <summary>Every .cs/.xaml/.js/.css under the scanned roots (bin and obj excluded), as paths relative to the root project folder with forward slashes.</summary>
    private static List<(string Project, string Relative, string Path)> ScannedFiles()
    {
        var files = new List<(string, string, string)>();
        foreach (var root in Roots)
        {
            var dir = Path.Combine(RepoPaths.AppRoot, "src", root);
            foreach (var path in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(dir, path).Replace('\\', '/');
                if (!Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)
                    || relative.StartsWith("bin/", StringComparison.Ordinal) || relative.StartsWith("obj/", StringComparison.Ordinal))
                {
                    continue;
                }

                files.Add((root, relative, path));
            }
        }

        return files;
    }

    [Fact]
    public void No_source_file_spells_a_track_color()
    {
        var scanned = ScannedFiles();
        scanned.Count.ShouldBeGreaterThan(100, "the glob must see the App and Presentation sources, or the guard checks nothing.");
        scanned.ShouldContain(f => f.Relative == "Assets/viewer/bridge.js");
        scanned.ShouldContain(f => f.Relative == "Controls/EntropySparkline.cs");
        scanned.ShouldContain(f => f.Relative == "Viewer/IgvLoadPlanner.cs");

        var offences = new List<string>();
        foreach (var (_, relative, path) in scanned.Where(f => !Allowed.ContainsKey(f.Relative)))
        {
            offences.AddRange(Literals(File.ReadAllText(path), Path.GetExtension(path).ToLowerInvariant()).Select(l => $"{relative} {l}"));
        }

        offences.ShouldBeEmpty("a source file spells a color itself; take it from TrackPalettes (issue #195), or allowlist it here and in ui_conventions.md with a reason.");
    }

    [Fact]
    public void Every_allowlist_entry_names_a_real_file_and_a_reason()
    {
        foreach (var (relative, reason) in Allowed)
        {
            reason.ShouldNotBeNullOrWhiteSpace();
            ScannedFiles().ShouldContain(f => f.Relative.Equals(relative, StringComparison.OrdinalIgnoreCase), $"the allowlist names {relative}, which is not scanned; remove the stale entry.");
        }
    }

    [Theory]
    [InlineData("signal.Color = Color.FromHex(\"#0F6CBD\");")]
    [InlineData("signal.Color = ScottPlot.Color.FromHex(\"0F6CBD\");")]
    [InlineData("line.Color = ScottPlot.Colors.Orange.WithAlpha(0.25);")]
    [InlineData("line.Color = Microsoft.UI.Colors.Red;")]
    [InlineData("var c = new Color(12, 34, 56);")]
    [InlineData("var c = new ScottPlot.Color(12, 34, 56);")]
    [InlineData("var c = ScottPlot.Color.FromRGB(12, 34, 56);")]
    [InlineData("var c = ColorHelper.FromArgb(255, 12, 34, 56);")]
    [InlineData("return { color: 'rgba(220, 60, 60, 0.6)' };")]
    [InlineData("var c = \"#B54708\";")]
    [InlineData("var c = \"#80B54708\";")]
    [InlineData("var x = 1; /* note */ var c = \"#B54708\";")]
    public void The_scanner_catches_a_planted_color(string line)
        => Literals(line).Count.ShouldBe(1);

    [Theory]
    [InlineData("plot.FigureBackground.Color = ScottPlot.Colors.White;")]
    [InlineData("var ink = dark ? Colors.White : Colors.Black;")]
    [InlineData("var ink = Microsoft.UI.Colors.Transparent;")]
    [InlineData("// the old line was #0F6CBD")]
    [InlineData("var x = 1; // the old line was #0F6CBD")]
    [InlineData("/* the old line was ScottPlot.Colors.Orange */ var x = 1;")]
    [InlineData("var u = \"https://run.deg/a\"; // rgba(1, 2, 3, 4)")]
    [InlineData("var c = ColorHelper.FromArgb(255, (byte)r, (byte)g, (byte)b);")]
    [InlineData("var c = ScottPlot.Color.FromHex(hex);")]
    public void The_scanner_leaves_paper_ink_comments_and_conversions_alone(string line)
        => Literals(line).ShouldBeEmpty();

    [Fact]
    public void A_multiline_block_comment_and_a_xaml_comment_hide_a_color_but_the_line_after_does_not()
    {
        Literals("/*\n  was #0F6CBD\n*/\nvar c = \"#0F6CBD\";").ShouldBe(["line 4: var c = \"#0F6CBD\";"]);
        Literals("<!-- #0F6CBD -->\n<Border Background=\"#0F6CBD\"/>", ".xaml").ShouldBe(["line 2: <Border Background=\"#0F6CBD\"/>"]);
        Literals("a { color: red; } /* #0F6CBD */\nb { color: #0F6CBD; }", ".css").ShouldBe(["line 2: b { color: #0F6CBD; }"]);
    }

    [Fact]
    public void Every_consumer_reads_the_palette_it_is_meant_to()
    {
        // Comments are stripped first, so a mention in a comment does not count as reading the palette.
        string Code(string relative) => StripComments(Read(relative), Path.GetExtension(relative));
        Code(Path.Combine("src", "DnaEntropyGraph.App", "Controls", "EntropyPlotBuilder.cs")).ShouldContain("PaletteColors");
        Code(Path.Combine("src", "DnaEntropyGraph.App", "Controls", "EntropyOverviewChart.cs")).ShouldContain("AppPalette.Current");
        Code(Path.Combine("src", "DnaEntropyGraph.App", "Controls", "EntropySparkline.cs")).ShouldContain("AppPalette.Current");
        Code(Path.Combine("src", "DnaEntropyGraph.App", "Services", "OverviewPngExporter.cs")).ShouldContain("_palettes.Current");
        Code(Path.Combine("src", "DnaEntropyGraph.Presentation", "ViewModels", "ViewerViewModel.cs")).ShouldContain("_palettes?.Current");
        Code(Path.Combine("src", "DnaEntropyGraph.Presentation", "ViewModels", "ViewerViewModel.cs")).ShouldContain(".ForViewer()");
        Code(Path.Combine("src", "DnaEntropyGraph.App", "Startup", "ServiceRegistration.cs")).ShouldContain("ITrackPaletteProvider");
        Code(Path.Combine("src", "DnaEntropyGraph.App", "Startup", "ServiceRegistration.cs")).ShouldContain("PaletteCacheInvalidator.Attach");
    }

    [Fact]
    public void A_mention_in_a_comment_is_not_reading_the_palette()
        => StripComments("// AppPalette.Current is read here\nvar x = 1;", ".cs").ShouldNotContain("AppPalette.Current");
    [Fact]
    public void The_viewer_backgrounds_are_the_ones_the_palettes_are_validated_on()
    {
        Read(Path.Combine("src", "DnaEntropyGraph.App", "Assets", "viewer", "viewer.css")).ShouldContain("background: " + PaletteBackgrounds.Light.ToLowerInvariant());
        var dark = Read(Path.Combine("src", "DnaEntropyGraph.App", "Assets", "viewer", "dark.css"));
        dark.ShouldContain("background: " + PaletteBackgrounds.Dark.ToLowerInvariant());
        // The filter the palette validator simulates (ViewerDarkFilter) is the one the page applies.
        dark.ShouldContain("filter: " + ViewerDarkFilter.CssFilter);
    }
}
