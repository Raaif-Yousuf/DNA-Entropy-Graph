using System.Text;
using System.Text.Json;
using DnaEntropyGraph.Cloud.Auth;

namespace DnaEntropyGraph.Cloud.Tests.Auth;

/// <summary>A reversible stand-in for DPAPI so the account tests run on any OS; the real one has its own Windows-only test.</summary>
internal sealed class XorProtector : ISecretProtector
{
    public byte[] Protect(byte[] plain) => plain.Select(b => (byte)(b ^ 0x5A)).ToArray();

    public byte[] Unprotect(byte[] protectedBytes) => protectedBytes.Select(b => (byte)(b ^ 0x5A)).ToArray();
}

/// <summary>A clock the test moves by hand.</summary>
internal sealed class ManualClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    public void Advance(TimeSpan by) => _now += by;

    public override DateTimeOffset GetUtcNow() => _now;
}

/// <summary>A temp app-data folder, a fake Google, and a service factory over both.</summary>
internal sealed class AuthHarness : IDisposable
{
    public const string ClientId = "test-client.apps.googleusercontent.com";

    public AuthHarness(bool writeClientFile = true)
    {
        Root = Path.Combine(Path.GetTempPath(), "deg-auth-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(Root);
        ClientFile = Path.Combine(Root, "oauth_client.local.json");
        if (writeClientFile)
        {
            File.WriteAllText(
                ClientFile,
                JsonSerializer.Serialize(new { installed = new { client_id = ClientId, client_secret = "test-secret", auth_uri = "https://accounts.google.com/o/oauth2/auth", token_uri = "https://oauth2.googleapis.com/token" } }),
                new UTF8Encoding(false));
        }
    }

    public string Root { get; }

    public string AuthDirectory => Path.Combine(Root, "auth");

    public string ClientFile { get; }

    public FakeGoogleOAuth Google { get; } = new();

    /// <summary>What a signed-in account with no chosen project answers (GoogleAccountOptions.ProjectIdUntilSelectionExists); null like a build with the real gateways.</summary>
    public string? FallbackProjectId { get; set; }

    public TimeSpan SignInTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>When set, the service uses this instead of the fake browser.</summary>
    public IBrowserLauncher? BrowserOverride { get; set; }

    /// <summary>When true the service gets no HTTP handler: Google's own HTTP stack, as in production.</summary>
    public bool UseProductionHttp { get; set; }

    /// <summary>When set, the service reads this clock (the locked-accounts-file retry window); otherwise the real one.</summary>
    public TimeProvider? Clock { get; set; }

    /// <summary>How long a save waits for another copy's lock; short in tests that hold it.</summary>
    public TimeSpan? SaveLockWait { get; set; }

    /// <summary>A service over this harness's folder; calling it twice is "the app restarted".</summary>
    public GoogleAccountService NewService() => new(new GoogleAccountOptions
    {
        SaveLockWait = SaveLockWait,
        TimeProvider = Clock ?? TimeProvider.System,
        AuthDirectory = AuthDirectory,
        ClientLoader = new OAuthClientLoader([ClientFile]),
        Browser = BrowserOverride ?? Google,
        Pages = new LoopbackPages(() => "done", () => "failed"),
        Protector = new XorProtector(),
        HttpHandler = UseProductionHttp ? null : Google,
        SignInTimeout = SignInTimeout,
        ProjectIdUntilSelectionExists = FallbackProjectId,
    });

    public async Task<GoogleAccountService> SignedInAsync(string sub, string email, GoogleAccountService? service = null)
    {
        service ??= NewService();
        Google.NextIdentity = new FakeIdentity(sub, email);
        await service.SignInAsync(CancellationToken.None);
        return service;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
