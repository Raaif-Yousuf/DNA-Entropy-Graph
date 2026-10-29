using System.Text;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// Issue #933, wired-to-nothing: a Google list loop that reads the next page token itself, instead of through <c>PageTokenGuard.Advance</c>, has no
/// repeated-token guard (one looped for ever, one silently truncated). In every <c>Rest/*.cs</c> but the helper: the token of a response may only be read as
/// the argument of <c>.Advance(</c> (in code, never in a comment or a string), no string may name the JSON member, a token may never be compared by hand,
/// and the guard is built once per walk, before the loop, never inside one (a guard built per page remembers nothing).
/// </summary>
public class PageTokenGuardWiringTests
{
    // Built at runtime so this file never contains the literals it forbids.
    private static readonly string NextToken = "Next" + "PageToken";
    private static readonly string GuardType = "PageToken" + "Guard";

    private static readonly Regex HandCompare = new(@"[=!]=\s*pageToken\b|\bpageToken\s*[=!]=", RegexOptions.Compiled);
    private static readonly Regex LoopKeyword = new(@"(^|[^\w])(do|for|foreach|while)\b", RegexOptions.Compiled);

    private static string RestDir => Path.Combine(RepoPaths.AppRoot, "src", "DnaEntropyGraph.Cloud", "Rest");

    /// <summary>The source with comments removed and the inside of every string and char literal blanked (newlines kept), plus the text of each string literal.</summary>
    internal static (string Code, List<string> Strings) Strip(string s)
    {
        var code = new StringBuilder(s.Length);
        var strings = new List<string>();
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '/')
            {
                while (i < s.Length && s[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < s.Length && !(s[i] == '*' && s[i + 1] == '/'))
                {
                    code.Append(s[i] == '\n' ? '\n' : ' ');
                    i++;
                }

                i += 2;
                continue;
            }

            if (c == '"' || ((c == '$' || c == '@') && i + 1 < s.Length && (s[i + 1] == '"' || s[i + 1] == '$' || s[i + 1] == '@')))
            {
                var start = i;
                var verbatim = false;
                while (i < s.Length && s[i] != '"')
                {
                    verbatim |= s[i] == '@';
                    i++;
                }

                var quotes = 0;
                while (i + quotes < s.Length && s[i + quotes] == '"')
                {
                    quotes++;
                }

                var raw = quotes >= 3;
                var body = new StringBuilder();
                i += raw ? quotes : 1;
                while (i < s.Length)
                {
                    if (raw)
                    {
                        if (s[i] == '"' && i + quotes <= s.Length && s.AsSpan(i, quotes).ToString() == new string('"', quotes))
                        {
                            i += quotes;
                            break;
                        }
                    }
                    else if (verbatim)
                    {
                        if (s[i] == '"' && i + 1 < s.Length && s[i + 1] == '"')
                        {
                            body.Append('"');
                            i += 2;
                            continue;
                        }

                        if (s[i] == '"')
                        {
                            i++;
                            break;
                        }
                    }
                    else
                    {
                        if (s[i] == '\\' && i + 1 < s.Length)
                        {
                            body.Append(s[i + 1]);
                            i += 2;
                            continue;
                        }

                        if (s[i] == '"')
                        {
                            i++;
                            break;
                        }
                    }

                    body.Append(s[i]);
                    code.Append(s[i] == '\n' ? '\n' : ' ');
                    i++;
                }

                strings.Add(body.ToString());
                code.Append("\"\"");
                _ = start;
                continue;
            }

            if (c == '\'' && i + 2 < s.Length)
            {
                var end = s[i + 1] == '\\' ? i + 3 : i + 2;
                if (end < s.Length && s[end] == '\'')
                {
                    code.Append("' '");
                    i = end + 1;
                    continue;
                }
            }

            code.Append(c);
            i++;
        }

        return (code.ToString(), strings);
    }

    internal static IEnumerable<(string File, int Line, string Text)> Violations(IEnumerable<(string Name, string Source)> files)
    {
        foreach (var (name, source) in files)
        {
            if (Path.GetFileName(name) == GuardType + ".cs")
            {
                continue;
            }

            var (code, strings) = Strip(source);
            foreach (var literal in strings.Where(t => t.Contains(NextToken, StringComparison.OrdinalIgnoreCase)))
            {
                yield return (name, 0, "string literal names the page token member: " + literal);
            }

            var lines = code.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var text = lines[i].Trim();
                if ((text.Contains(NextToken, StringComparison.OrdinalIgnoreCase) && !text.Contains(".Advance(", StringComparison.Ordinal)) || HandCompare.IsMatch(text))
                {
                    yield return (name, i + 1, text);
                }
            }

            foreach (var line in GuardsBuiltInsideLoops(code))
            {
                yield return (name, line, "a " + GuardType + " is built inside a loop");
            }
        }
    }

    /// <summary>Lines where <c>new PageTokenGuard(</c> sits inside a loop body (braced, nested at any depth, or the single statement of a loop).</summary>
    private static IEnumerable<int> GuardsBuiltInsideLoops(string code)
    {
        var stack = new Stack<bool>();
        var pending = new StringBuilder();
        var parens = 0;
        var line = 1;
        var needle = "new " + GuardType + "(";
        for (var i = 0; i < code.Length; i++)
        {
            var c = code[i];
            switch (c)
            {
                case '\n':
                    line++;
                    pending.Append(' ');
                    break;
                case '{':
                    stack.Push(LoopKeyword.IsMatch(pending.ToString()));
                    pending.Clear();
                    break;
                case '}':
                    if (stack.Count > 0)
                    {
                        stack.Pop();
                    }

                    pending.Clear();
                    break;
                case ';':
                    if (parens <= 0)
                    {
                        pending.Clear();
                    }
                    else
                    {
                        pending.Append(c);
                    }

                    break;
                default:
                    parens += c == '(' ? 1 : c == ')' ? -1 : 0;
                    pending.Append(c);
                    if (c == '(' && pending.Length >= needle.Length && pending.ToString(pending.Length - needle.Length, needle.Length) == needle
                        && (stack.Contains(true) || LoopKeyword.IsMatch(pending.ToString())))
                    {
                        yield return line;
                    }

                    break;
            }
        }
    }

    private static List<(string Name, string Source)> RestSources()
        => Directory.GetFiles(RestDir, "*.cs").Select(p => (Path.GetFileName(p), File.ReadAllText(p))).ToList();

    [Fact]
    public void No_rest_file_reads_a_next_page_token_outside_the_paging_guard()
    {
        Violations(RestSources()).ShouldBeEmpty();
    }

    [Fact]
    public void The_scanner_sees_every_rest_file_and_every_list_loop_so_a_clean_tree_is_not_vacuous()
    {
        var sources = RestSources();
        sources.Select(s => s.Name).ShouldContain("GoogleRestClient.cs", "files that are not gateways are scanned too");
        sources.Select(s => s.Name).ShouldContain(GuardType + ".cs");
        sources.Count(s => s.Name.EndsWith("Gateway.cs", StringComparison.Ordinal)).ShouldBeGreaterThanOrEqualTo(8);

        // Counted in code only (comments and strings are stripped), so neither can raise them.
        var code = sources.Where(s => s.Name != GuardType + ".cs").Select(s => Strip(s.Source).Code).ToList();
        var built = code.Sum(c => Regex.Matches(c, Regex.Escape("new " + GuardType + "(")).Count);
        var advanced = code.Sum(c => Regex.Matches(c, Regex.Escape(".Advance(")).Count);
        built.ShouldBeGreaterThanOrEqualTo(11, "11 list loops were guarded when this was written; the floor only goes up");
        advanced.ShouldBe(built, "one guard, one Advance per walk");
    }

    [Theory]
    [InlineData("pageToken = page.{T};")]
    [InlineData("var next = string.IsNullOrEmpty(page.{T}) ? null : page.{T};")]
    [InlineData("while (!string.IsNullOrEmpty(response.{T}));")]
    [InlineData("var t = doc.RootElement.GetProperty(\"{t}\");")]
    [InlineData("if (next is not null && next == pageToken) { return zones; }")]
    [InlineData("pageToken = page.{T}; // .Advance(")]
    [InlineData("pageToken = page.{T}; /* .Advance( */")]
    [InlineData("var note = \".Advance(\"; pageToken = page.{T};")]
    public void A_planted_unguarded_loop_is_found(string planted)
    {
        var source = "class P {\n" + Fill(planted) + "\n}";

        Violations([("Planted.cs", source)]).ShouldNotBeEmpty();
    }

    [Fact]
    public void A_planted_unguarded_read_is_found_in_a_rest_file_that_is_not_a_gateway()
    {
        Violations([("GoogleSomethingElse.cs", "class P {\n" + Fill("pageToken = page.{T};") + "\n}")]).Count().ShouldBe(1);
    }

    [Theory]
    [InlineData("do\n{\n    var g = new {G}(\"x\");\n    pageToken = g.Advance(page.{T});\n}\nwhile (pageToken is not null);")]
    [InlineData("foreach (var z in zones)\n{\n    if (z.Length > 0)\n    {\n        var g = new {G}(\"x\");\n    }\n}")]
    [InlineData("for (var i = 0; i < 3; i++) var g = new {G}(\"x\");")]
    [InlineData("while (true)\n{\n    Run(() => new {G}(\"x\"));\n}")]
    public void A_planted_guard_built_inside_a_loop_is_found(string planted)
    {
        Violations([("Planted.cs", "class P {\nvoid M() {\n" + Fill(planted) + "\n}\n}")]).ShouldContain(v => v.Text.Contains("inside a loop"));
    }

    [Fact]
    public void The_guarded_walk_a_comment_a_string_and_the_helper_itself_are_not_violations()
    {
        var guarded = "var paging = new {G}(\"the list\");\nstring? pageToken = null;\ndo\n{\n    pageToken = paging.Advance(page.{T});\n}\nwhile (pageToken is not null);";
        Violations([("Planted.cs", "class P {\nvoid M() {\n" + Fill(guarded) + "\n}\n}")]).ShouldBeEmpty();
        Violations([("Planted.cs", "// reads {T} == pageToken and new {G}( in a do loop".Replace("{T}", NextToken).Replace("{G}", GuardType))]).ShouldBeEmpty();
        Violations([("Planted.cs", "var s = \"" + NextToken + "\";".Replace("{T}", NextToken))]).ShouldNotBeEmpty("a string naming the member is a JSON read");
        Violations([(GuardType + ".cs", Fill("var t = page.GetProperty(\"{t}\"); x = page.{T};"))]).ShouldBeEmpty();
    }

    private static string Fill(string text)
        => text.Replace("{T}", NextToken).Replace("{t}", char.ToLowerInvariant(NextToken[0]) + NextToken[1..]).Replace("{G}", GuardType);
}
