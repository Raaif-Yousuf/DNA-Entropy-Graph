using System.Text.RegularExpressions;

namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// The job id convention this app applies to every id it generates itself:
/// <c>yyyymmdd-hhmmss-&lt;6 lowercase base32 chars&gt;</c> - sortable by
/// creation time, and GCP-label/resource-name safe by construction (Hard
/// Rule 9's <c>deg-&lt;jobId&gt;</c> VM naming, issue #55). This is a
/// convention, not the only contract a job id must satisfy:
/// <see cref="VmSpec.EnsurePreconditions"/> only requires that whatever job
/// id it is given is itself safe to use as a label value and inside a
/// Compute Engine resource name (see <see cref="VmSpec"/>'s own GCP-safety
/// checks); a job id that does not follow this exact convention is not, by
/// itself, an error VmSpec will reject.
/// </summary>
public static class JobId
{
    private const string Base32Alphabet = "abcdefghijklmnopqrstuvwxyz234567";

    private static readonly Regex ConventionPattern = new(@"^\d{8}-\d{6}-[a-z2-7]{6}$", RegexOptions.Compiled);

    /// <summary>
    /// Generates a new job id following the convention above.
    /// <paramref name="random"/> is injected so a test can assert an exact
    /// value; production callers pass <see cref="Random.Shared"/>.
    /// </summary>
    public static string NewId(DateTimeOffset utcNow, Random random)
    {
        ArgumentNullException.ThrowIfNull(random);

        var timestamp = utcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        Span<char> suffix = stackalloc char[6];
        for (var i = 0; i < suffix.Length; i++)
        {
            suffix[i] = Base32Alphabet[random.Next(Base32Alphabet.Length)];
        }

        return $"{timestamp}-{new string(suffix)}";
    }

    /// <summary>True when <paramref name="jobId"/> follows the <c>yyyymmdd-hhmmss-&lt;6 base32&gt;</c> convention exactly.</summary>
    public static bool MatchesConvention(string jobId) => jobId is not null && ConventionPattern.IsMatch(jobId);
}
