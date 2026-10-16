using System.Globalization;
using System.Net;
using System.Text.Json;

namespace DnaEntropyGraph.Core.Inputs;

/// <summary>
/// <see cref="IAccessionLookup"/> over NCBI E-utilities <c>esummary</c> (issue #684): one small request that says whether the record exists
/// and how long it is. Same politeness as the worker's <c>readers/entrez.py</c>: no more than 3 requests a second without a key, a 30 second
/// timeout, 429 and 5xx retried once (after the server's Retry-After, at most 30 seconds, else 2), nothing else retried. The body is
/// never logged and the URL carries only the accession.
/// </summary>
public sealed class NcbiAccessionLookup : IAccessionLookup
{
    private const string EsummaryUrl = "https://eutils.ncbi.nlm.nih.gov/entrez/eutils/esummary.fcgi";
    private static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(340);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultBackoff = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RetryAfterMax = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<long> _nowMs;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long? _lastRequestMs;

    /// <param name="delay">Waits; tests replace it. Default <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</param>
    /// <param name="nowMs">A monotonic clock in milliseconds; tests replace it.</param>
    public NcbiAccessionLookup(HttpClient http, Func<TimeSpan, CancellationToken, Task>? delay = null, Func<long>? nowMs = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _delay = delay ?? Task.Delay;
        _nowMs = nowMs ?? (() => Environment.TickCount64);
    }

    public async Task<AccessionLookupResult> LookupAsync(string accession, string? range, CancellationToken cancellationToken)
    {
        var check = AccessionInput.Check(accession, range);
        if (check.Problem is not null)
        {
            throw new ArgumentException("The accession and range must pass AccessionInput.Check before they are looked up.", nameof(accession));
        }

        var url = EsummaryUrl + "?db=nuccore&retmode=json&id=" + Uri.EscapeDataString(check.Accession);
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            await PaceAsync(cancellationToken).ConfigureAwait(false);
            HttpResponseMessage response;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(RequestTimeout);
                response = await _http.GetAsync(url, timeout.Token).ConfigureAwait(false);
                using (response)
                {
                    var code = response.StatusCode;
                    if (code == HttpStatusCode.OK)
                    {
                        var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                        return Interpret(body, check);
                    }

                    var retryable = code == HttpStatusCode.TooManyRequests || (int)code >= 500;
                    if (retryable && attempt == 1)
                    {
                        await _delay(RetryDelay(response), cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    return AccessionLookupResult.Failed(code switch
                    {
                        HttpStatusCode.TooManyRequests => AccessionLookupProblem.RateLimited,
                        >= HttpStatusCode.InternalServerError => AccessionLookupProblem.NcbiTrouble,
                        HttpStatusCode.BadRequest or HttpStatusCode.NotFound => AccessionLookupProblem.NotFound,
                        _ => AccessionLookupProblem.Unreachable,
                    });
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return AccessionLookupResult.Failed(AccessionLookupProblem.TimedOut);
            }
            catch (HttpRequestException)
            {
                return AccessionLookupResult.Failed(AccessionLookupProblem.Unreachable);
            }
        }

        return AccessionLookupResult.Failed(AccessionLookupProblem.NcbiTrouble);
    }

    private static TimeSpan RetryDelay(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("Retry-After", out var values)
            && long.TryParse(values.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            var wanted = TimeSpan.FromSeconds(Math.Max(1, Math.Min(seconds, (long)RetryAfterMax.TotalSeconds)));
            return wanted;
        }

        return DefaultBackoff;
    }

    private async Task PaceAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_lastRequestMs is { } last)
            {
                var wait = MinInterval - TimeSpan.FromMilliseconds(_nowMs() - last);
                if (wait > TimeSpan.Zero)
                {
                    await _delay(wait, cancellationToken).ConfigureAwait(false);
                }
            }

            _lastRequestMs = _nowMs();
        }
        finally
        {
            _gate.Release();
        }
    }

    private static AccessionLookupResult Interpret(string body, AccessionCheck asked)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("result", out var result)
                || !result.TryGetProperty("uids", out var uids)
                || uids.ValueKind != JsonValueKind.Array)
            {
                // NCBI answers an unknown id with {"error": ...} and no result at all.
                return document.RootElement.TryGetProperty("error", out _)
                    ? AccessionLookupResult.Failed(AccessionLookupProblem.NotFound)
                    : AccessionLookupResult.Failed(AccessionLookupProblem.NcbiTrouble);
            }

            var uid = uids.EnumerateArray().Select(u => u.GetString()).FirstOrDefault(u => !string.IsNullOrEmpty(u));
            if (uid is null || !result.TryGetProperty(uid, out var record) || record.TryGetProperty("error", out _))
            {
                return AccessionLookupResult.Failed(AccessionLookupProblem.NotFound);
            }

            var resolved = record.TryGetProperty("accessionversion", out var version) ? (version.GetString() ?? string.Empty).ToUpperInvariant() : string.Empty;
            var sameRecord = asked.Accession.Contains('.', StringComparison.Ordinal)
                ? resolved == asked.Accession
                : resolved.Split('.')[0] == asked.Accession;
            if (!sameRecord || !record.TryGetProperty("slen", out var slen) || !slen.TryGetInt64(out var length) || length < 1)
            {
                return AccessionLookupResult.Failed(AccessionLookupProblem.NotFound);
            }

            var bases = length;
            if (asked.Range is { } range)
            {
                var parts = range.Split('-');
                var start = long.Parse(parts[0], CultureInfo.InvariantCulture);
                var end = long.Parse(parts[1], CultureInfo.InvariantCulture);
                if (start > length)
                {
                    return AccessionLookupResult.Failed(AccessionLookupProblem.RangeOutsideRecord);
                }

                bases = Math.Min(end, length) - start + 1;
            }

            return AccessionLimits.IsTooLarge(bases)
                ? AccessionLookupResult.Failed(AccessionLookupProblem.TooLarge)
                : AccessionLookupResult.Found(bases, resolved);
        }
        catch (JsonException)
        {
            return AccessionLookupResult.Failed(AccessionLookupProblem.NcbiTrouble);
        }
        catch (InvalidOperationException)
        {
            return AccessionLookupResult.Failed(AccessionLookupProblem.NcbiTrouble);
        }
    }
}
