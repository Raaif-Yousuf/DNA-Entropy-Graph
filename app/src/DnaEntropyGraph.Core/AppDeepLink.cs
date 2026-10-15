namespace DnaEntropyGraph.Core;

/// <summary>
/// The argument text that carries "open this run" from a Windows notification, or from a second launch of the exe, into the running
/// app (issue #107). It comes from outside the app, so a job id is accepted only in the plain shape the app itself makes
/// (letters, digits, '-' and '_'), never a path or anything that could reach a page as more than an id.
/// </summary>
public static class AppDeepLink
{
    public const string RunKey = "run";

    private const int MaxJobIdLength = 64;

    /// <summary>The argument text a notification carries for <paramref name="jobId"/>.</summary>
    public static string ForRun(string jobId)
        => IsPlainId(jobId) ? RunKey + "=" + jobId : throw new ArgumentException("A job id is letters, digits, '-' and '_' only.", nameof(jobId));

    /// <summary>Finds the run in <paramref name="arguments"/> ("run=ID", alone or among others separated by ';' or '&amp;'); false when there is none or it is not a plain id.</summary>
    public static bool TryGetRunId(string? arguments, out string jobId)
    {
        jobId = string.Empty;
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return false;
        }

        foreach (var part in arguments.Split([';', '&'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = part.IndexOf('=', StringComparison.Ordinal);
            if (separator > 0 && part[..separator] == RunKey && IsPlainId(part[(separator + 1)..]))
            {
                jobId = part[(separator + 1)..];
                return true;
            }
        }

        return false;
    }

    private static bool IsPlainId(string? value)
        => !string.IsNullOrEmpty(value) && value.Length <= MaxJobIdLength && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
