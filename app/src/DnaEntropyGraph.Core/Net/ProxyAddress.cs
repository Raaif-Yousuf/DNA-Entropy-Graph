using System.Text.RegularExpressions;

namespace DnaEntropyGraph.Core.Net;

/// <summary>Reads the manual proxy box. Only <c>host:port</c> (an optional <c>http://</c> in front) is accepted; a password in the address is refused so it can never reach settings.json or a log.</summary>
public static partial class ProxyAddress
{
    public static bool TryParse(string? text, out Uri uri)
    {
        uri = null!;
        var trimmed = (text ?? string.Empty).Trim();
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed["http://".Length..];
        }

        if (!HostAndPort().IsMatch(trimmed))
        {
            return false;
        }

        var port = int.Parse(trimmed[(trimmed.LastIndexOf(':') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
        if (port is < 1 or > 65535 || !Uri.TryCreate("http://" + trimmed, UriKind.Absolute, out var parsed) || string.IsNullOrEmpty(parsed.Host))
        {
            return false;
        }

        uri = parsed;
        return true;
    }

    [GeneratedRegex(@"^[A-Za-z0-9]([A-Za-z0-9.\-]*[A-Za-z0-9])?:[0-9]{1,5}$")]
    private static partial Regex HostAndPort();
}
