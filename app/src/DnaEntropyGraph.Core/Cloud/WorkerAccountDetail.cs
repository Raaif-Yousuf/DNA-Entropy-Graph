using System.Text.RegularExpressions;

namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// Issue #604: the service account a run's VM was asked to run as, carried in <c>RunRecord.ErrorDetail</c> as a trailing
/// <c>[vm-account=...]</c> mark (no new column). It is the app's worker account, or the project's default Compute Engine
/// account when an organization policy refused the former, and the owner request for an actAs refusal must name the one
/// the refusal is about. A service account's email, never the user's.
/// </summary>
public static partial class WorkerAccountDetail
{
    private const string Prefix = "[vm-account=";

    public static string Append(string? detail, string? accountEmail)
        => string.IsNullOrWhiteSpace(accountEmail) ? detail ?? string.Empty : $"{detail} {Prefix}{accountEmail}]".TrimStart();

    /// <summary>The account in <paramref name="detail"/>, or null when there is no well-formed mark.</summary>
    public static string? TryRead(string? detail)
    {
        if (detail is null)
        {
            return null;
        }

        var match = Mark().Match(detail);
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex(@"\[vm-account=([A-Za-z0-9._\-]+@[A-Za-z0-9._\-]+)\]")]
    private static partial Regex Mark();
}
