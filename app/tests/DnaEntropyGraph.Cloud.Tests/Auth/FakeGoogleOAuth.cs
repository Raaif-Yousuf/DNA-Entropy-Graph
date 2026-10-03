using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DnaEntropyGraph.Cloud.Auth;

namespace DnaEntropyGraph.Cloud.Tests.Auth;

/// <summary>One identity the fake Google hands out: the <c>sub</c> and email that go into the id token.</summary>
internal sealed record FakeIdentity(string Sub, string Email);

/// <summary>
/// An HTTP-level fake of Google's OAuth server (it answers Google's real token and revoke URLs, so the app's default endpoints are what is exercised) and of the user's browser (issue #48). No network, no browser, no
/// Google: the token and revoke endpoints are this handler, and the "browser" parses the authorization URL the
/// app opened and calls the app's real loopback listener back. The token endpoint checks the PKCE proof, so a
/// request that drops the <c>code_verifier</c> is refused exactly as Google refuses it.
/// </summary>
internal sealed class FakeGoogleOAuth : HttpMessageHandler, IBrowserLauncher
{
    public const string TokenUrl = "https://oauth2.googleapis.com/token";
    public const string RevokeUrl = "https://oauth2.googleapis.com/revoke";
    public const string AuthorizationUrl = "https://accounts.google.com/o/oauth2/v2/auth";

    private readonly object _gate = new();
    private readonly Dictionary<string, (string Challenge, FakeIdentity Identity)> _codes = new();
    private readonly Dictionary<string, FakeIdentity> _refreshTokens = new();
    private readonly HashSet<string> _revokedRefreshTokens = new();
    private int _counter;

    /// <summary>What the user does on Google's page.</summary>
    public BrowserBehaviour Behaviour { get; set; } = BrowserBehaviour.Approve;

    /// <summary>The account the next successful sign-in is for.</summary>
    public FakeIdentity NextIdentity { get; set; } = new("1001", "first@example.test");

    public int ExpiresInSeconds { get; set; } = 3600;

    /// <summary>When true the revoke endpoint answers 500, as Google does when it is unreachable or failing.</summary>
    public bool RevokeFails { get; set; }

    /// <summary>When true every token request fails at the transport level (no network).</summary>
    public bool NetworkDown { get; set; }

    /// <summary>When true every token request times out (HttpClient surfaces that as a TaskCanceledException the caller did not ask for).</summary>
    public bool TokenRequestsTimeOut { get; set; }

    /// <summary>When true every token request throws an unrelated InvalidOperationException, standing in for a bug of ours or a library's.</summary>
    public bool TokenRequestsThrowABug { get; set; }

    /// <summary>When false the token response has no <c>id_token</c>.</summary>
    public bool ReturnIdToken { get; set; } = true;

    /// <summary>When false the first token response has no <c>refresh_token</c>.</summary>
    public bool ReturnRefreshToken { get; set; } = true;

    public List<Dictionary<string, string>> AuthorizationRequests { get; } = [];

    public List<Dictionary<string, string>> TokenRequests { get; } = [];

    public List<string> RevokedTokens { get; } = [];

    public int BrowserLaunches => AuthorizationRequests.Count;

    /// <summary>The refresh token the server issued for <paramref name="sub"/>'s latest sign-in.</summary>
    public string RefreshTokenFor(string sub) => _refreshTokens.Last(pair => pair.Value.Sub == sub).Key;

    /// <summary>Google stops honouring every refresh token (the user revoked access at myaccount.google.com).</summary>
    public void RevokeEverythingAtGoogle()
    {
        lock (_gate)
        {
            foreach (var token in _refreshTokens.Keys)
            {
                _revokedRefreshTokens.Add(token);
            }
        }
    }

    public Task LaunchAsync(Uri authorizationUrl, CancellationToken cancellationToken)
    {
        var query = ParseQuery(authorizationUrl.Query);
        lock (_gate)
        {
            AuthorizationRequests.Add(query);
        }

        // Run on another thread: the app's listener answers this very call, and the app is still inside
        // LaunchAsync's caller until this returns.
        _ = Task.Run(async () =>
        {
            if (Behaviour == BrowserBehaviour.Hang)
            {
                return;
            }

            var code = "code-" + Interlocked.Increment(ref _counter);
            lock (_gate)
            {
                _codes[code] = (query["code_challenge"], NextIdentity);
            }

            var redirect = query["redirect_uri"];
            var callback = Behaviour switch
            {
                BrowserBehaviour.Deny => $"{redirect}?error=access_denied&state={Uri.EscapeDataString(query["state"])}",
                BrowserBehaviour.WrongStateOnly or BrowserBehaviour.WrongStateThenReal => $"{redirect}?code={code}&state=attacker-chosen",
                _ => $"{redirect}?code={code}&state={Uri.EscapeDataString(query["state"])}",
            };

            using var http = new HttpClient();
            using var _ = await http.GetAsync(callback, CancellationToken.None);
            if (Behaviour == BrowserBehaviour.WrongStateThenReal)
            {
                // The stray request got its 400; now the genuine redirect arrives.
                using var real = await http.GetAsync($"{redirect}?code={code}&state={Uri.EscapeDataString(query["state"])}", CancellationToken.None);
            }
        }, CancellationToken.None);

        return Task.CompletedTask;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        var form = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        // Google's client puts the revoked token in the query string and the code/refresh grants in the form body.
        var fields = ParseQuery(form);
        foreach (var (name, value) in ParseQuery(request.RequestUri.Query))
        {
            fields[name] = value;
        }

        if (url.StartsWith(TokenUrl, StringComparison.Ordinal))
        {
            if (NetworkDown)
            {
                throw new HttpRequestException("fake: no network");
            }

            if (TokenRequestsThrowABug)
            {
                throw new InvalidOperationException("bug");
            }

            if (TokenRequestsTimeOut)
            {
                throw new TaskCanceledException("fake: the request timed out");
            }

            lock (_gate)
            {
                TokenRequests.Add(fields);
                return HandleToken(fields);
            }
        }

        if (url.StartsWith(RevokeUrl, StringComparison.Ordinal))
        {
            lock (_gate)
            {
                if (RevokeFails)
                {
                    return Json(HttpStatusCode.InternalServerError, new { error = "backend_error" });
                }

                RevokedTokens.Add(fields["token"]);
                _revokedRefreshTokens.Add(fields["token"]);
                return Json(HttpStatusCode.OK, new { });
            }
        }

        return Json(HttpStatusCode.NotFound, new { error = "unexpected_url", url });
    }

    private HttpResponseMessage HandleToken(Dictionary<string, string> fields)
    {
        switch (fields.GetValueOrDefault("grant_type"))
        {
            case "authorization_code":
            {
                if (!_codes.Remove(fields.GetValueOrDefault("code") ?? string.Empty, out var issued))
                {
                    return Json(HttpStatusCode.BadRequest, new { error = "invalid_grant" });
                }

                var verifier = fields.GetValueOrDefault("code_verifier");
                if (verifier is null || Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))) != issued.Challenge)
                {
                    return Json(HttpStatusCode.BadRequest, new { error = "invalid_grant", error_description = "Missing or wrong code_verifier" });
                }

                var refresh = "rt-" + issued.Identity.Sub + "-" + Interlocked.Increment(ref _counter);
                _refreshTokens[refresh] = issued.Identity;
                var body = new Dictionary<string, object?>
                {
                    ["access_token"] = "at-" + Interlocked.Increment(ref _counter),
                    ["expires_in"] = ExpiresInSeconds,
                    ["token_type"] = "Bearer",
                    ["scope"] = "openid https://www.googleapis.com/auth/userinfo.email https://www.googleapis.com/auth/cloud-platform",
                };
                if (ReturnRefreshToken)
                {
                    body["refresh_token"] = refresh;
                }

                if (ReturnIdToken)
                {
                    body["id_token"] = IdToken(issued.Identity);
                }

                return Json(HttpStatusCode.OK, body);
            }

            case "refresh_token":
            {
                var token = fields.GetValueOrDefault("refresh_token") ?? string.Empty;
                if (!_refreshTokens.ContainsKey(token) || _revokedRefreshTokens.Contains(token))
                {
                    return Json(HttpStatusCode.BadRequest, new { error = "invalid_grant", error_description = "Token has been expired or revoked." });
                }

                // Google's refresh response carries no refresh_token: the client must keep the one it has.
                return Json(HttpStatusCode.OK, new Dictionary<string, object?>
                {
                    ["access_token"] = "at-" + Interlocked.Increment(ref _counter),
                    ["expires_in"] = 3600,
                    ["token_type"] = "Bearer",
                });
            }

            default:
                return Json(HttpStatusCode.BadRequest, new { error = "unsupported_grant_type" });
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, object body) => new(status)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
    };

    /// <summary>An unsigned JWT: the app reads the claims and trusts the TLS channel it came over, as OpenID Connect allows for a code-flow id token.</summary>
    public static string IdToken(FakeIdentity identity)
    {
        var header = Base64Url(Encoding.UTF8.GetBytes("{\"alg\":\"none\"}"));
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { iss = "https://accounts.google.com", sub = identity.Sub, email = identity.Email }));
        return $"{header}.{payload}.";
    }

    public static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static Dictionary<string, string> ParseQuery(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in text.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            result[Uri.UnescapeDataString(parts[0].Replace('+', ' '))] = parts.Length > 1 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : string.Empty;
        }

        return result;
    }
}

internal enum BrowserBehaviour
{
    Approve,
    Deny,
    /// <summary>Only a request with the wrong state ever arrives.</summary>
    WrongStateOnly,

    /// <summary>A request with the wrong state arrives first, then the real redirect.</summary>
    WrongStateThenReal,
    Hang,
}
