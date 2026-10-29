using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// Issue #239, wired-to-nothing: a bare HTTP client built anywhere in app/src ignores the proxy policy and looks right until a lab behind a typed proxy
/// says it times out (the Cloud Quotas client and the NCBI lookup were exactly that). Every outbound stack is built in the files below and nowhere else.
/// </summary>
public class ProxyBareClientGuardTests
{
    private static readonly Regex BareClient = new(@"\bnew\s+(HttpClient|SocketsHttpHandler|HttpClientHandler)\s*(\(|\{)", RegexOptions.Compiled);

    /// <summary>File name (under app/src) to why it may build one. These are the design of the guard, not an allowlist to get a red build green.</summary>
    private static readonly Dictionary<string, string> Builders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ProxyHttpClientFactory.cs"] = "The one handler every Google client and the proxied helper use: its SocketsHttpHandler carries the PolicyWebProxy.",
        ["ProxiedHttpClient.cs"] = "Wraps a ProxyHttpClientFactory handler in an HttpClient for callers outside Google.Apis (NCBI lookup).",
        ["GoogleRestClient.cs"] = "Wraps a ProxyHttpClientFactory handler (or a scripted test handler) in the bearer-token HttpClient for Cloud Quotas.",
    };

    /// <summary>Every construction site in a source text, with its 1-based line. Scanned over the whole text, not line by line: <c>new SocketsHttpHandler</c> followed by <c>{</c> on the next line is a construction too.</summary>
    internal static IEnumerable<(int Line, string Text)> Sites(string source)
    {
        var lines = source.Split('\n');
        foreach (Match m in BareClient.Matches(source))
        {
            var line = source.AsSpan(0, m.Index).Count('\n');
            var text = lines[line].TrimStart();
            if (!text.StartsWith("//", StringComparison.Ordinal))
            {
                yield return (line + 1, text.Trim());
            }
        }
    }

    internal static IEnumerable<(string File, int Line, string Text)> Violations(IEnumerable<(string Name, string Source)> files)
        => files.Where(f => !Builders.ContainsKey(Path.GetFileName(f.Name))).SelectMany(f => Sites(f.Source).Select(s => (f.Name, s.Line, s.Text)));

    private static IEnumerable<(string, string)> RealSources() => RepoPaths.AllCSharpFiles.Select(p => (p, File.ReadAllText(p)));

    [Fact]
    public void No_file_in_app_src_builds_a_bare_http_client_outside_the_proxy_builders()
    {
        Violations(RealSources()).ShouldBeEmpty();
    }

    [Fact]
    public void The_scanner_sees_the_real_builders_so_a_clean_tree_is_not_vacuous()
    {
        var sources = RealSources().ToList();
        sources.Count.ShouldBeGreaterThan(300);
        foreach (var builder in Builders.Keys)
        {
            sources.Where(s => Path.GetFileName(s.Item1) == builder).ShouldNotBeEmpty($"{builder} should exist in app/src");
        }

        // Without the exception list the real tree is NOT clean: the builders do contain the patterns.
        var raw = sources.Sum(s => Sites(s.Item2).Count());
        raw.ShouldBeGreaterThanOrEqualTo(3);
    }

    [Theory]
    [InlineData("var h = new HttpClient();")]
    [InlineData("var h = new HttpClient { MaxResponseContentBufferSize = 5 };")]
    [InlineData("var h = new SocketsHttpHandler();")]
    [InlineData("var h = new HttpClientHandler();")]
    [InlineData("var h = new HttpClient (handler);")]
    [InlineData("var h = new SocketsHttpHandler\n{\n    UseProxy = false,\n};")]
    public void A_planted_bare_client_is_found(string planted)
    {
        Violations([("Planted.cs", "class P {\n" + planted + "\n}")]).Count().ShouldBe(1);
    }

    [Fact]
    public void A_commented_mention_and_a_listed_builder_are_not_violations()
    {
        Violations([("Planted.cs", "// new HttpClient()\n/// <c>new SocketsHttpHandler()</c>")]).ShouldBeEmpty();
        Violations([("ProxyHttpClientFactory.cs", "var s = new SocketsHttpHandler();")]).ShouldBeEmpty();
    }
}
