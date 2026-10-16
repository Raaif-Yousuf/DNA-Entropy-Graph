using System.Net;
using System.Text;
using DnaEntropyGraph.Core.Inputs;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests.Inputs;

/// <summary>
/// Issue #684 / Hard Rule 2: before any cloud resource exists the app asks NCBI (esummary) whether the record is there and fits the
/// worker's size limit, so nobody pays for a VM to learn the accession does not exist. No live NCBI: a stub handler answers.
/// </summary>
public sealed class NcbiAccessionLookupTests
{
    private static string Summary(string accessionVersion, long length, string uid = "1798174254")
        => $$$$"""
            {"header":{"type":"esummary"},"result":{"uids":["{{{{uid}}}}"],"{{{{uid}}}}":{"uid":"{{{{uid}}}}","accessionversion":"{{{{accessionVersion}}}}","slen":{{{{length}}}},"title":"A record"}}}
            """;

    private const string Empty = """{"header":{"type":"esummary"},"result":{"uids":[]}}""";

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Status(HttpStatusCode code, string? retryAfter = null)
    {
        var response = new HttpResponseMessage(code) { Content = new StringContent(string.Empty) };
        if (retryAfter is not null)
        {
            response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
        }

        return response;
    }

    private sealed class Stub(params Func<HttpRequestMessage, HttpResponseMessage>[] answers) : HttpMessageHandler
    {
        private int _next;

        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            var answer = answers[Math.Min(_next++, answers.Length - 1)];
            return Task.FromResult(answer(request));
        }
    }

    private sealed class Clock
    {
        public long Now { get; set; } = 1_000_000;

        public List<TimeSpan> Waits { get; } = [];

        public Task Delay(TimeSpan span, CancellationToken token)
        {
            Waits.Add(span);
            Now += (long)span.TotalMilliseconds;
            return Task.CompletedTask;
        }
    }

    private static (NcbiAccessionLookup Lookup, Stub Handler, Clock Clock) Make(params Func<HttpRequestMessage, HttpResponseMessage>[] answers)
    {
        var handler = new Stub(answers);
        var clock = new Clock();
        return (new NcbiAccessionLookup(new HttpClient(handler), clock.Delay, () => clock.Now), handler, clock);
    }

    [Fact]
    public async Task A_record_that_exists_reports_its_length_and_resolved_version_from_one_esummary_request()
    {
        var (lookup, handler, _) = Make(_ => Ok(Summary("NC_045512.2", 29903)));

        var result = await lookup.LookupAsync("NC_045512.2", null, CancellationToken.None);

        result.IsValid.ShouldBeTrue();
        result.Length.ShouldBe(29903);
        result.Resolved.ShouldBe("NC_045512.2");
        var url = handler.Requests.ShouldHaveSingleItem();
        url.Host.ShouldBe("eutils.ncbi.nlm.nih.gov");
        url.AbsolutePath.ShouldBe("/entrez/eutils/esummary.fcgi");
        url.Query.ShouldContain("db=nuccore");
        url.Query.ShouldContain("id=NC_045512.2");
        url.Query.ShouldContain("retmode=json");
    }

    [Theory]
    [InlineData("100-500", 401)]
    [InlineData("29900-40000", 4)]
    [InlineData("1-29903", 29903)]
    public async Task A_range_is_measured_and_clipped_to_the_record(string range, long expected)
    {
        var (lookup, _, _) = Make(_ => Ok(Summary("NC_045512.2", 29903)));

        var result = await lookup.LookupAsync("NC_045512.2", range, CancellationToken.None);

        result.Problem.ShouldBeNull();
        result.Length.ShouldBe(expected);
    }

    [Fact]
    public async Task A_range_that_starts_after_the_record_ends_is_refused_as_outside_the_record()
    {
        var (lookup, _, _) = Make(_ => Ok(Summary("NC_045512.2", 29903)));

        (await lookup.LookupAsync("NC_045512.2", "30000-40000", CancellationToken.None)).Problem.ShouldBe(AccessionLookupProblem.RangeOutsideRecord);
    }

    [Theory]
    [InlineData(Empty)]
    [InlineData("""{"result":{"uids":["5"],"5":{"uid":"5","error":"cannot get document summary"}}}""")]
    [InlineData("""{"error":"Invalid uid NC_999999.9 at position=0"}""")]
    public async Task An_accession_nobody_has_is_not_found_and_costs_one_request(string body)
    {
        var (lookup, handler, _) = Make(_ => Ok(body));

        var result = await lookup.LookupAsync("NC_999999.9", null, CancellationToken.None);

        result.Problem.ShouldBe(AccessionLookupProblem.NotFound);
        handler.Requests.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task A_400_or_404_is_not_found_and_not_retried(HttpStatusCode code)
    {
        var (lookup, handler, _) = Make(_ => Status(code));

        (await lookup.LookupAsync("NC_045512.2", null, CancellationToken.None)).Problem.ShouldBe(AccessionLookupProblem.NotFound);
        handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task NCBI_answering_with_a_different_record_than_asked_is_not_found()
    {
        var (lookup, _, _) = Make(_ => Ok(Summary("NC_000001.11", 1000)));

        (await lookup.LookupAsync("NC_045512.2", null, CancellationToken.None)).Problem.ShouldBe(AccessionLookupProblem.NotFound);
    }

    [Fact]
    public async Task An_unversioned_request_is_resolved_to_the_current_version_of_the_same_record()
    {
        var (lookup, _, _) = Make(_ => Ok(Summary("NC_045512.2", 29903)));

        var result = await lookup.LookupAsync("NC_045512", null, CancellationToken.None);

        result.Problem.ShouldBeNull();
        result.Resolved.ShouldBe("NC_045512.2");
    }

    [Theory]
    [InlineData(40_000_000L, null, false)] // 1.25 bytes a base is 50 MB: under the 64 MiB the worker reads
    [InlineData(53_687_091L, null, false)] // 64 MiB / 1.25, the largest whole record that can fit
    [InlineData(53_687_092L, null, true)]
    [InlineData(200_000_000L, null, true)]
    [InlineData(200_000_000L, "1-1000000", false)] // a range brings it under the limit
    public async Task A_record_too_large_for_the_workers_size_limit_is_refused_unless_a_range_brings_it_under(long length, string? range, bool tooLarge)
    {
        var (lookup, _, _) = Make(_ => Ok(Summary("CP000001.1", length)));

        var result = await lookup.LookupAsync("CP000001.1", range, CancellationToken.None);

        result.Problem.ShouldBe(tooLarge ? AccessionLookupProblem.TooLarge : null);
    }

    [Fact]
    public void The_size_limit_is_the_workers_own_read_from_entrez_py()
    {
        var source = File.ReadAllText(Path.Combine(ContractFixtures.RepoRoot, "worker", "src", "dna_entropy", "readers", "entrez.py"), Encoding.UTF8);
        source.ShouldContain("_MAX_BODY_BYTES = 64 * 1024 * 1024", Case.Sensitive, "the worker's cap changed: update AccessionLimits.MaxBodyBytes with it");
        AccessionLimits.MaxBodyBytes.ShouldBe(64L * 1024 * 1024);
    }

    [Fact]
    public async Task Rate_limited_once_it_waits_the_retry_after_and_asks_again()
    {
        var (lookup, handler, clock) = Make(_ => Status(HttpStatusCode.TooManyRequests, "5"), _ => Ok(Summary("NC_045512.2", 29903)));

        var result = await lookup.LookupAsync("NC_045512.2", null, CancellationToken.None);

        result.Problem.ShouldBeNull();
        handler.Requests.Count.ShouldBe(2);
        clock.Waits.ShouldContain(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Rate_limited_twice_reports_rate_limited_after_exactly_two_requests()
    {
        var (lookup, handler, _) = Make(_ => Status(HttpStatusCode.TooManyRequests));

        (await lookup.LookupAsync("NC_045512.2", null, CancellationToken.None)).Problem.ShouldBe(AccessionLookupProblem.RateLimited);
        handler.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task The_retry_after_is_capped_at_thirty_seconds_and_a_missing_one_waits_two()
    {
        var (lookup, _, clock) = Make(_ => Status(HttpStatusCode.TooManyRequests, "9999"), _ => Status(HttpStatusCode.ServiceUnavailable), _ => Ok(Summary("NC_045512.2", 1000)));
        await lookup.LookupAsync("NC_045512.2", null, CancellationToken.None);
        clock.Waits.ShouldContain(TimeSpan.FromSeconds(30));

        var (second, _, clock2) = Make(_ => Status(HttpStatusCode.ServiceUnavailable), _ => Ok(Summary("NC_045512.2", 1000)));
        await second.LookupAsync("NC_045512.2", null, CancellationToken.None);
        clock2.Waits.ShouldContain(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task NCBI_trouble_twice_reports_ncbi_trouble()
    {
        var (lookup, handler, _) = Make(_ => Status(HttpStatusCode.BadGateway));

        (await lookup.LookupAsync("NC_045512.2", null, CancellationToken.None)).Problem.ShouldBe(AccessionLookupProblem.NcbiTrouble);
        handler.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_refused_request_or_a_network_failure_is_unreachable_and_not_retried()
    {
        var (refused, h1, _) = Make(_ => Status(HttpStatusCode.Forbidden));
        (await refused.LookupAsync("NC_045512.2", null, CancellationToken.None)).Problem.ShouldBe(AccessionLookupProblem.Unreachable);
        h1.Requests.Count.ShouldBe(1);

        var (offline, h2, _) = Make(_ => throw new HttpRequestException("no route"));
        (await offline.LookupAsync("NC_045512.2", null, CancellationToken.None)).Problem.ShouldBe(AccessionLookupProblem.Unreachable);
        h2.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_request_that_times_out_is_timed_out_and_a_cancel_by_the_caller_is_not()
    {
        var (slow, _, _) = Make(_ => throw new TaskCanceledException("timed out", new TimeoutException()));
        (await slow.LookupAsync("NC_045512.2", null, CancellationToken.None)).Problem.ShouldBe(AccessionLookupProblem.TimedOut);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var (any, _, _) = Make(_ => Ok(Summary("NC_045512.2", 1000)));
        await Should.ThrowAsync<OperationCanceledException>(() => any.LookupAsync("NC_045512.2", null, cts.Token));
    }

    [Fact]
    public async Task Requests_keep_the_workers_pace_of_three_a_second_without_a_key()
    {
        var (lookup, _, clock) = Make(_ => Ok(Summary("NC_045512.2", 1000)));

        await lookup.LookupAsync("NC_045512.2", null, CancellationToken.None);
        await lookup.LookupAsync("NC_045512.2", null, CancellationToken.None);

        clock.Waits.ShouldContain(TimeSpan.FromMilliseconds(340));
    }

    [Fact]
    public async Task Garbage_that_is_not_json_is_ncbi_trouble_and_never_an_exception()
    {
        var (lookup, _, _) = Make(_ => Ok("<html>maintenance</html>"));

        (await lookup.LookupAsync("NC_045512.2", null, CancellationToken.None)).Problem.ShouldBe(AccessionLookupProblem.NcbiTrouble);
    }

    [Fact]
    public async Task An_unchecked_accession_is_a_programming_error_and_no_request_is_made()
    {
        var (lookup, handler, _) = Make(_ => Ok(Empty));

        await Should.ThrowAsync<ArgumentException>(() => lookup.LookupAsync("NC_045512.2&db=x", null, CancellationToken.None));
        handler.Requests.ShouldBeEmpty();
    }
}
