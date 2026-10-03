using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// What the results bucket is, independent of how Google is asked to make it (issue #53; docs/cloud_design.md section 16).
/// One bucket per Google project, named <c>deg-&lt;projectNumber&gt;-&lt;rand6&gt;</c> (the <c>deg-</c> prefix is what
/// the worker's IAM condition keys on), found again by label, never by a fixed name (Hard Rule 9).
/// </summary>
public static class ResultsBucket
{
    public const string NamePrefix = "deg-";

    /// <summary>The object at the bucket root that records what created the bucket and which retention it was asked for.</summary>
    public const string ConfigObject = "app-config.json";

    /// <summary>Every job's objects live under this prefix; the retention rule applies to it.</summary>
    public const string JobsPrefix = "jobs/";

    /// <summary>The model-weights cache lives under this prefix; it has its own, longer rule.</summary>
    public const string CachePrefix = "cache/";

    /// <summary>The "Cloud results retention" default; <c>RunOptions.CloudResultsRetentionDays</c> takes its default from here.</summary>
    public const int DefaultRetentionDays = 90;

    public const int CacheRetentionDays = 365;

    /// <summary>The longest retention accepted (ten years): the choices offered are 7/30/90/365 days, and anything past this is a typo, not a policy.</summary>
    public const int MaxRetentionDays = 3650;

    /// <summary>
    /// Refuses a retention that would delete job results at once (0) or that Google rejects (negative), or that is absurdly
    /// long, before any request is built. The lifecycle rule is the only thing that deletes results (Hard Rule 14).
    /// </summary>
    public static void ValidateRetentionDays(int days)
    {
        if (days is < 1 or > MaxRetentionDays)
        {
            throw new ArgumentOutOfRangeException(nameof(days), days, $"Results retention must be between 1 and {MaxRetentionDays} days.");
        }
    }

    /// <summary>The multi-region the bucket is created in when nothing else is asked for (the "US" region group).</summary>
    public const string DefaultLocation = "US";

    /// <summary>The <c>lifecycle</c> label value: the bucket outlives every run and is deleted only by the user.</summary>
    public const string LifecycleLabelValue = "results";

    private const string Base32Alphabet = "abcdefghijklmnopqrstuvwxyz234567";

    private static readonly Regex ProjectNumberPattern = new("^[0-9]{1,20}$", RegexOptions.Compiled);

    /// <summary><c>deg-&lt;projectNumber&gt;-&lt;6 lowercase base32 chars&gt;</c>; <paramref name="random"/> is injected so a test can assert an exact name.</summary>
    public static string NewName(string projectNumber, Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        if (!ProjectNumberPattern.IsMatch(projectNumber ?? string.Empty))
        {
            throw new ArgumentException("A Google project number is digits only.", nameof(projectNumber));
        }

        var suffix = new StringBuilder(6);
        for (var i = 0; i < 6; i++)
        {
            suffix.Append(Base32Alphabet[random.Next(Base32Alphabet.Length)]);
        }

        return $"{NamePrefix}{projectNumber}-{suffix}";
    }

    /// <summary>
    /// The labels a bucket carries: the standard set (Hard Rule 10) minus <c>job-id</c> and <c>model</c>, which name one
    /// run and one model while a bucket serves every run (the same exemption the project's own labels have), and its
    /// <c>lifecycle</c> is <see cref="LifecycleLabelValue"/>. DECISION (agent-made, reversible): see #582 and the Rule 9/10 carve-out in docs/hard_rules.md.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Labels(string installationId, string appVersion)
    {
        if (string.IsNullOrWhiteSpace(installationId))
        {
            throw new ArgumentException("The installation id label is required (Hard Rule 10).", nameof(installationId));
        }

        var version = Regex.Replace((appVersion ?? string.Empty).Trim().ToLowerInvariant(), "[^a-z0-9_-]", "-");
        return new Dictionary<string, string>
        {
            ["app"] = VmSpec.AppLabelValue,
            ["installation-id"] = installationId,
            ["app-version"] = version.Length == 0 ? "unknown" : version.Length > 63 ? version[..63] : version,
            ["lifecycle"] = LifecycleLabelValue,
        };
    }

    /// <summary>The body of <see cref="ConfigObject"/>: plain JSON, UTF-8, LF, no sequence data.</summary>
    public static string ConfigJson(string installationId, string appVersion, int retentionDays, DateTimeOffset createdUtc)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, NewLine = "\n" }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema", 1);
            writer.WriteString("installationId", installationId);
            writer.WriteString("appVersion", appVersion);
            writer.WriteNumber("resultsRetentionDays", retentionDays);
            writer.WriteNumber("cacheRetentionDays", CacheRetentionDays);
            writer.WriteString("createdUtc", createdUtc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture));
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }
}
