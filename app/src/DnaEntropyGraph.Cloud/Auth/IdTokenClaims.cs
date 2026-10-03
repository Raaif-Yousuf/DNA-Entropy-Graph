using System.Text;
using System.Text.Json;

namespace DnaEntropyGraph.Cloud.Auth;

/// <summary>
/// The two claims the app reads from an id token. The token arrives over TLS directly from Google's token endpoint
/// in answer to a request this app made, which OpenID Connect Core 3.1.3.7 allows to be trusted without checking the
/// signature, so no key fetch is made. Never used to authorize anything: a Google Cloud call is authorized by the
/// access token, which Google checks.
/// </summary>
internal static class IdTokenClaims
{
    public static (string Sub, string? Email)? Read(string? idToken)
    {
        var parts = idToken?.Split('.');
        if (parts is null || parts.Length < 2)
        {
            return null;
        }

        try
        {
            var padded = parts[1].Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');
            using var document = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(padded)));
            var sub = document.RootElement.TryGetProperty("sub", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
            var email = document.RootElement.TryGetProperty("email", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
            return sub is null ? null : (sub, email);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }
}
