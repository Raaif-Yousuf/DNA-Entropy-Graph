using System.Globalization;
using System.Text.RegularExpressions;

namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// Issue #929: how many of the user's changed result files a download renamed aside (Hard Rule 14, issue #588), carried in
/// <c>RunRecord.ErrorDetail</c> as a trailing <c>[kept-aside=N]</c> mark (no new column, the same shape as <see cref="WorkerAccountDetail"/>).
/// The primary error code stays the run's headline; the Runs row and the run page read this mark on their own, so the count is
/// visible whichever code the run ended with.
/// </summary>
public static partial class KeptAsideDetail
{
    private const string Prefix = "[kept-aside=";

    public static string Append(string? detail, int count)
        => count <= 0 ? detail ?? string.Empty : $"{detail} {Prefix}{count.ToString(CultureInfo.InvariantCulture)}]".TrimStart();

    /// <summary>
    /// <paramref name="detail"/> with its mark replaced by one for <paramref name="count"/> (no mark at all for 0 or less). The run row's count is kept this way while a download
    /// is under way (issue #1059), so it is on the row whatever ends the run.
    /// </summary>
    public static string Set(string? detail, int count)
        => Append(Mark().Replace(detail ?? string.Empty, string.Empty).Trim(), count);

    /// <summary>
    /// <paramref name="detail"/> carrying the larger of its own count and <paramref name="previousDetail"/>'s (issue #1059): a terminal write built from an exception message or from a
    /// path that knew nothing of the rename must not wipe the count the row already holds.
    /// </summary>
    public static string? Carry(string? detail, string? previousDetail)
    {
        var previous = TryRead(previousDetail);
        return previous > TryRead(detail) ? Set(detail, previous) : detail;
    }

    /// <summary>The count in <paramref name="detail"/>, or 0 when there is no well-formed positive mark. The LAST mark wins (the app appends its own after any text).</summary>
    public static int TryRead(string? detail)
    {
        if (detail is null)
        {
            return 0;
        }

        var matches = Mark().Matches(detail);
        return matches.Count > 0 && int.TryParse(matches[^1].Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var count) ? count : 0;
    }

    [GeneratedRegex(@"\[kept-aside=([0-9]{1,6})\]")]
    private static partial Regex Mark();
}
