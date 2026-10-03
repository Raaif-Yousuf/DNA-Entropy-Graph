using DnaEntropyGraph.Cloud;
using DnaEntropyGraph.Cloud.Auth;
using DnaEntropyGraph.Cloud.Rest;
using DnaEntropyGraph.Core.Cloud;

namespace DnaEntropyGraph.CloudCli;

/// <summary>The two gateways the verbs use.</summary>
public sealed record CloudCliSession(IComputeGateway Compute, IStorageGateway Storage);

/// <summary>Thrown by a session factory when no account is signed in. The app reads it as exit code <see cref="CloudCliExitCode.NotSignedIn"/>.</summary>
public sealed class CloudCliNotSignedInException : Exception
{
    public CloudCliNotSignedInException()
        : base("No Google account is signed in.")
    {
    }
}

/// <summary>Opens a session for one project. A seam so the verbs run over a fake or a scripted HTTP handler without a signed-in account.</summary>
public interface ICloudCliSessionFactory
{
    /// <exception cref="CloudCliNotSignedInException">No account is signed in.</exception>
    CloudCliSession Open(string projectId, string? installationId);
}

/// <summary>The signed-in account as the console needs it: whether there is one, the access token, and the single refresh the resilience pipeline may ask for.</summary>
public sealed record CloudCliAuth(bool IsSignedIn, IGcpAccessTokenSource Tokens, ICloudTokenRefresher Refresher);

/// <summary>
/// The REAL gateways (<see cref="GoogleCloudGateways.Create"/>, the one place production switches from the fake to Google), behind the
/// same resilience pipeline the app uses, authorised by the account the app signed in (Hard Rule 12: never a key file). This is
/// the console's only way to a gateway, so a console that printed rows without ever sending a request would be wired to nothing:
/// <c>CloudCliEndToEndTests</c> asserts the recorded HTTP requests.
/// </summary>
public sealed class GoogleCloudCliSessionFactory : ICloudCliSessionFactory
{
    private readonly Func<CloudCliAuth> _auth;
    private readonly HttpMessageHandler? _handler;
    private readonly bool _instant;

    /// <param name="auth">Read on every <see cref="Open"/>.</param>
    /// <param name="handler">Null in production (Google's own HTTP stack). A test passes the scripted handler.</param>
    /// <param name="instant">A test sets it so retries and operation polls do not wait.</param>
    public GoogleCloudCliSessionFactory(Func<CloudCliAuth> auth, HttpMessageHandler? handler = null, bool instant = false)
    {
        _auth = auth;
        _handler = handler;
        _instant = instant;
    }

    public CloudCliSession Open(string projectId, string? installationId)
    {
        var auth = _auth();
        if (!auth.IsSignedIn)
        {
            throw new CloudCliNotSignedInException();
        }

        var retry = _instant ? new CloudRetryOptions { BaseDelay = TimeSpan.Zero, MaxDelay = TimeSpan.Zero } : new CloudRetryOptions();
        var pipeline = new CloudCallPipeline(retry, auth.Refresher, new CloudRetryLog());
        var gateways = GoogleCloudGateways.Create(
            auth.Tokens,
            pipeline,
            new GoogleCloudOptions
            {
                HttpHandler = _handler,
                Delay = _instant ? (_, _) => Task.CompletedTask : null,
                InstallationId = installationId,
                AppVersion = CloudCliParser.DefaultAppVersion,
            },
            () => projectId);
        return new CloudCliSession(gateways.Compute, gateways.Storage);
    }
}

/// <summary>The console's production wiring: the account store the app itself writes.</summary>
public static class CloudCliHost
{
    /// <summary><c>%LOCALAPPDATA%\DNAEntropyGraph</c>, where the app keeps <c>auth\</c> (the same folder the app's own sign-in uses).</summary>
    public static string DefaultAppDataDirectory()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DNAEntropyGraph");

    /// <summary>
    /// Reads the account the app signed in (DPAPI token files under <c>auth\</c> in <paramref name="appDataDirectory"/>). This process
    /// never signs anyone in: its browser launcher refuses, so a signed-out store ends as "Sign in with the app first".
    /// </summary>
    public static CloudCliAuth OpenAppAccount(string appDataDirectory)
    {
        var service = new GoogleAccountService(new GoogleAccountOptions
        {
            AuthDirectory = Path.Combine(appDataDirectory, "auth"),
            ClientLoader = new OAuthClientLoader(OAuthClientLoader.DefaultCandidates(appDataDirectory, AppContext.BaseDirectory)),
            Browser = new NoBrowser(),
            Pages = new LoopbackPages(() => string.Empty, () => string.Empty),
        });
        return new CloudCliAuth(service.IsSignedIn, service, service);
    }

    /// <summary>The production entry point: the real account store, the real gateways. <paramref name="appDataDirectory"/> null means the app's own folder.</summary>
    public static Task<int> RunAsync(IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr, string? appDataDirectory, CancellationToken cancellationToken)
    {
        var directory = appDataDirectory ?? DefaultAppDataDirectory();
        var sessions = new GoogleCloudCliSessionFactory(() => OpenAppAccount(directory));
        return new CloudCliApp(sessions, TimeProvider.System, Random.Shared).RunAsync(args, stdout, stderr, cancellationToken);
    }

    private sealed class NoBrowser : IBrowserLauncher
    {
        public Task LaunchAsync(Uri url, CancellationToken cancellationToken)
            => throw new InvalidOperationException("The console never signs in.");
    }
}
