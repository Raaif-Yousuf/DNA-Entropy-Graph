using System.Text.Json;
using DnaEntropyGraph.Core.Cloud;

namespace DnaEntropyGraph.Cloud.Auth;

/// <summary>The OAuth Desktop-app client the build was made with. The secret is "not confidential" per Google for a desktop client, but is still never committed (Hard Rule 12).</summary>
public sealed record OAuthClient(string ClientId, string ClientSecret);

/// <summary>
/// Finds and reads <c>oauth_client.local.json</c>, the file Google's console downloads for a Desktop-app client:
/// <c>{"installed":{"client_id":...,"client_secret":...}}</c>. In dev it is <c>app/secrets/</c> (gitignored); a
/// release build copies the CI-injected one next to the executable. A missing or malformed file is an
/// <see cref="AccountAuthException"/> with a code the UI turns into one actionable sentence, never a crash.
/// </summary>
public sealed class OAuthClientLoader
{
    public const string FileName = "oauth_client.local.json";

    private readonly IReadOnlyList<string> _candidates;

    public OAuthClientLoader(IReadOnlyList<string> candidatePaths)
    {
        _candidates = candidatePaths;
    }

    /// <summary>
    /// Next to the executable (release), in the app data folder, then <c>secrets/</c> and <c>app/secrets/</c> in each
    /// folder above the executable, up to eight levels (a dev build run from <c>app/src/.../bin/Debug</c>).
    /// </summary>
    public static IReadOnlyList<string> DefaultCandidates(string appDataDirectory, string baseDirectory)
    {
        var candidates = new List<string>
        {
            Path.Combine(baseDirectory, FileName),
            Path.Combine(appDataDirectory, FileName),
        };

        var directory = new DirectoryInfo(baseDirectory);
        for (var level = 0; level < 8 && directory is not null; level++, directory = directory.Parent)
        {
            candidates.Add(Path.Combine(directory.FullName, "secrets", FileName));
            candidates.Add(Path.Combine(directory.FullName, "app", "secrets", FileName));
        }

        return candidates;
    }

    public OAuthClient Load()
    {
        var path = _candidates.FirstOrDefault(File.Exists)
            ?? throw new AccountAuthException(AuthErrorCodes.OAuthClientMissing, "no candidate file exists");

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var installed = document.RootElement.GetProperty("installed");
            var id = installed.GetProperty("client_id").GetString();
            var secret = installed.GetProperty("client_secret").GetString();
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(secret))
            {
                throw new AccountAuthException(AuthErrorCodes.OAuthClientInvalid, "client_id or client_secret is empty");
            }

            return new OAuthClient(id, secret);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IOException)
        {
            // GetProperty on a non-object throws InvalidOperationException; a missing property KeyNotFoundException.
            throw new AccountAuthException(AuthErrorCodes.OAuthClientInvalid, "not a Desktop-app client download", ex);
        }
    }
}
