using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// Issue #933, wired-to-nothing: a Google list loop that reads the next page token itself, instead of through <c>PageTokenGuard.Advance</c>, has no
/// repeated-token guard (one looped for ever, one silently truncated). In <c>Rest/*Gateway.cs</c> the token of a response may only be read as the argument
/// of <c>.Advance(</c>, and a token may never be compared by hand.
/// </summary>
public class PageTokenGuardWiringTests
{
    // Built at runtime so this file never contains the literals it forbids.
    private static readonly string NextToken = "Next" + "PageToken";

    private static readonly Regex HandCompare = new(@"[=!]=\s*pageToken\b|\bpageToken\s*[=!]=", RegexOptions.Compiled);

    private static string RestDir => Path.Combine(RepoPaths.AppRoot, "src", "DnaEntropyGraph.Cloud", "Rest");

    internal static IEnumerable<(string File, int Line, string Text)> Violations(IEnumerable<(string Name, string Source)> files)
    {
        foreach (var (name, source) in files)
        {
            var lines = source.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var text = lines[i].Trim();
                if (text.StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }

                var readsToken = text.Contains(NextToken, StringComparison.OrdinalIgnoreCase);
                if ((readsToken && !text.Contains(".Advance(", StringComparison.Ordinal)) || HandCompare.IsMatch(text))
                {
                    yield return (name, i + 1, text);
                }
            }
        }
    }

    private static List<(string Name, string Source)> Gateways()
        => Directory.GetFiles(RestDir, "*Gateway.cs").Select(p => (Path.GetFileName(p), File.ReadAllText(p))).ToList();

    [Fact]
    public void No_rest_gateway_reads_a_next_page_token_outside_the_paging_guard()
    {
        Violations(Gateways()).ShouldBeEmpty();
    }

    [Fact]
    public void The_scanner_sees_every_list_loop_so_a_clean_tree_is_not_vacuous()
    {
        var gateways = Gateways();
        gateways.Count.ShouldBeGreaterThanOrEqualTo(8);
        var guarded = gateways.Sum(g => Regex.Matches(g.Source, Regex.Escape(".Advance(")).Count);
        guarded.ShouldBe(11, "one Advance per list loop in Rest/ (the Cloud.Tests ListPagingGuardTests table has the same 11 rows); a new loop raises both");
        File.Exists(Path.Combine(RestDir, "PageTokenGuard.cs")).ShouldBeTrue();
    }

    [Theory]
    [InlineData("pageToken = page.{T};")]
    [InlineData("var next = string.IsNullOrEmpty(page.{T}) ? null : page.{T};")]
    [InlineData("while (!string.IsNullOrEmpty(response.{T}));")]
    [InlineData("var t = doc.RootElement.GetProperty(\"{t}\");")]
    [InlineData("if (next is not null && next == pageToken) { return zones; }")]
    public void A_planted_unguarded_loop_is_found(string planted)
    {
        var source = "class P {\n" + planted.Replace("{T}", NextToken).Replace("{t}", char.ToLowerInvariant(NextToken[0]) + NextToken[1..]) + "\n}";

        Violations([("Planted.cs", source)]).Count().ShouldBe(1);
    }

    [Fact]
    public void The_guarded_read_a_comment_and_the_helper_itself_are_not_violations()
    {
        Violations([("Planted.cs", "pageToken = paging.Advance(page." + NextToken + ");")]).ShouldBeEmpty();
        Violations([("Planted.cs", "// reads " + NextToken + " == pageToken")]).ShouldBeEmpty();
        Violations([("Planted.cs", "pageToken = paging.Advance(document.RootElement);")]).ShouldBeEmpty();
    }
}
