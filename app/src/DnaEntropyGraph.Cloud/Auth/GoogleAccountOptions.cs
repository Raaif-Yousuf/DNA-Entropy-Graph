namespace DnaEntropyGraph.Cloud.Auth;

/// <summary>Everything <see cref="GoogleAccountService"/> needs from its surroundings, so a test (or Guards.Tests) can aim every file and URL at a temp folder and a fake.</summary>
public sealed class GoogleAccountOptions
{
    /// <summary><c>%LOCALAPPDATA%\DNAEntropyGraph\auth</c> in production: the token files and <c>accounts.json</c>.</summary>
    public required string AuthDirectory { get; init; }

    public required OAuthClientLoader ClientLoader { get; init; }

    public required IBrowserLauncher Browser { get; init; }

    public required LoopbackPages Pages { get; init; }

    public ISecretProtector Protector { get; init; } = new DpapiSecretProtector();

    /// <summary>Null in production (Google's own HTTP stack, real Google URLs). A test passes a fake that answers those same URLs.</summary>
    public HttpMessageHandler? HttpHandler { get; init; }

    /// <summary>
    /// What <see cref="GoogleAccountService.SelectedProjectId"/> answers while signed in with no project chosen. A chosen
    /// project (issue #520) always wins. Production still sets it to the id the fake used, so a run on an account that
    /// never chose a project still reaches the (fake, not-connected) gateways and fails as cloud_not_connected. It goes
    /// when the real gateways are wired (#609): then no project means no_project, and the picker is the way out.
    /// </summary>
    public string? ProjectIdUntilSelectionExists { get; init; }

    /// <summary>How long the browser page may stay unfinished before the sign-in gives up.</summary>
    public TimeSpan SignInTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>The clock for the locked-accounts-file retry window (issue #616); tests move it by hand.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}
