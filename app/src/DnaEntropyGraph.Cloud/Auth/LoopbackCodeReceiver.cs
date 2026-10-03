using System.Net;
using System.Security.Cryptography;
using System.Text;
using DnaEntropyGraph.Core.Cloud;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Requests;
using Google.Apis.Auth.OAuth2.Responses;

namespace DnaEntropyGraph.Cloud.Auth;

/// <summary>The text of the page the browser lands on after Google redirects back. Functions, because the app resolves them from <c>Resources.resw</c> when needed (Hard Rule 13), not at start-up.</summary>
public sealed record LoopbackPages(Func<string> Success, Func<string> Failure);

/// <summary>
/// The loopback half of RFC 8252 for a desktop app: a listener on <c>127.0.0.1</c> at a free port, the authorization
/// URL opened through <see cref="IBrowserLauncher"/>, and the one redirect back read from it. Replaces Google's
/// <c>LocalServerCodeReceiver</c> because that one starts the browser itself, which leaves nothing to inject in a
/// test, and because this one also binds the redirect to a random <c>state</c> (docs/threat_model.md).
/// </summary>
public sealed class LoopbackCodeReceiver : ICodeReceiver, IDisposable
{
    private readonly IBrowserLauncher _browser;
    private readonly LoopbackPages _pages;
    private readonly TimeSpan _timeout;
    private HttpListener? _listener;
    private string? _redirectUri;

    public LoopbackCodeReceiver(IBrowserLauncher browser, LoopbackPages pages, TimeSpan timeout)
    {
        _browser = browser;
        _pages = pages;
        _timeout = timeout;
    }

    /// <summary>Starts the listener on first read: Google's installed-app helper reads this before it builds the authorization URL.</summary>
    public string RedirectUri
    {
        get
        {
            if (_redirectUri is null)
            {
                Start();
            }

            return _redirectUri!;
        }
    }

    public async Task<AuthorizationCodeResponseUrl> ReceiveCodeAsync(AuthorizationCodeRequestUrl url, CancellationToken taskCancellationToken)
    {
        _ = RedirectUri;
        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        url.State = state;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(taskCancellationToken);
        timeout.CancelAfter(_timeout);
        try
        {
            await _browser.LaunchAsync(url.Build(), taskCancellationToken).ConfigureAwait(false);
            while (true)
            {
                var context = await _listener!.GetContextAsync().WaitAsync(timeout.Token).ConfigureAwait(false);
                var query = context.Request.QueryString;
                var received = query.AllKeys.Where(k => k is not null).ToDictionary(k => k!, k => query[k]!, StringComparer.Ordinal);

                // The browser also asks for /favicon.ico and the like. Only the redirect carries a code or an error.
                if (!received.ContainsKey("code") && !received.ContainsKey("error"))
                {
                    Respond(context, HttpStatusCode.NotFound, string.Empty);
                    continue;
                }

                if (received.GetValueOrDefault("state") != state)
                {
                    Respond(context, HttpStatusCode.BadRequest, _pages.Failure());
                    throw new AccountAuthException(AuthErrorCodes.SigninFailed, "the redirect's state did not match");
                }

                var failed = received.ContainsKey("error");
                Respond(context, failed ? HttpStatusCode.BadRequest : HttpStatusCode.OK, failed ? _pages.Failure() : _pages.Success());
                return new AuthorizationCodeResponseUrl(received);
            }
        }
        catch (OperationCanceledException) when (!taskCancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("the sign-in page was not completed in time");
        }
    }

    public void Dispose()
    {
        _listener?.Close();
        _listener = null;
    }

    private void Start()
    {
        // A free port is found by binding one and handing it over; another program can take it in between, so retry.
        for (var attempt = 0; ; attempt++)
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
            }
            catch (HttpListenerException) when (attempt < 5)
            {
                listener.Close();
                continue;
            }

            _listener = listener;
            _redirectUri = $"http://127.0.0.1:{port}/";
            return;
        }
    }

    private static void Respond(HttpListenerContext context, HttpStatusCode status, string text)
    {
        var body = Encoding.UTF8.GetBytes($"<!doctype html><html><head><meta charset=\"utf-8\"><title></title></head><body>{WebUtility.HtmlEncode(text)}</body></html>");
        context.Response.StatusCode = (int)status;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = body.Length;
        context.Response.OutputStream.Write(body);
        context.Response.Close();
    }
}
