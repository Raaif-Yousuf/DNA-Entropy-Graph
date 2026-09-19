using System.Text.RegularExpressions;

namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// The request to create one VM. Hard Rule 9: never a shared singleton
/// cloud resource - every VM is named <c>deg-&lt;jobId&gt;</c>
/// (<see cref="VmName"/>) and discovered by label, never by a fixed name.
/// Hard Rule 10: every VM carries the standard labels (<c>app</c>,
/// <c>installation-id</c>, <c>job-id</c>, <c>model</c>, <c>app-version</c>,
/// <c>lifecycle</c>) and a <c>maxRunDuration</c> plus
/// <c>instanceTerminationAction</c>. <see cref="EnsurePreconditions"/> is
/// the guard (issue #55, building on #68's first cut): called by every real
/// gateway - and by <see cref="DnaEntropyGraph.Cloud.FakeGcp"/>, exactly the
/// same way - before a Compute Insert request is ever built, so an
/// unlabelled resource, or one the real API would itself reject, can never
/// reach the API in the first place.
/// </summary>
public sealed record VmSpec(
    string ProjectId,
    string InstallationId,
    string JobId,
    string Model,
    string AppVersion,
    string Lifecycle,
    string MachineType,
    TimeSpan MaxRunDuration,
    string TerminationAction)
{
    /// <summary>The <c>app</c> label's fixed value - every DNA Entropy Graph resource carries the same one.</summary>
    public const string AppLabelValue = "dna-entropy-graph";

    /// <summary>
    /// A Compute Engine label VALUE: lowercase letters, digits, underscores
    /// and hyphens only, 1-63 characters (real GCP constraint - label keys
    /// and values may not contain a dot, a space or an uppercase letter).
    /// </summary>
    private static readonly Regex GcpLabelValuePattern = new("^[a-z0-9_-]{1,63}$", RegexOptions.Compiled);

    /// <summary>
    /// A Compute Engine resource NAME: starts with a lowercase letter, ends
    /// with a lowercase letter or digit, contains only lowercase letters,
    /// digits and hyphens in between, at most 63 characters.
    /// </summary>
    private static readonly Regex GcpResourceNamePattern = new("^[a-z]([-a-z0-9]{0,61}[a-z0-9])?$", RegexOptions.Compiled);

    /// <summary>
    /// The VM's name (Hard Rule 9's <c>deg-&lt;jobId&gt;</c> naming,
    /// D13 in docs/cloud_design.md). Computed, never stored separately, so
    /// it can never drift from the job id every other lookup uses.
    /// </summary>
    public string VmName => $"deg-{JobId}";

    /// <summary>
    /// Throws naming the first missing or invalid precondition. Never
    /// silently defaults or silently drops a bad value - an optional field
    /// with a sensible default is exactly the "hides a missing caller"
    /// shape wired-to-nothing warns about, and a spec that passes this
    /// check but that the real API would still reject is a worse failure
    /// than one this guard catches itself (a wasted round trip, and a
    /// support conversation instead of a compile-time-adjacent error
    /// message).
    /// </summary>
    public void EnsurePreconditions()
    {
        // Fields required to build any request at all (not themselves
        // Hard Rule 10 labels - ProjectId is the target project, not a
        // label on the resource created inside it).
        RequireNonEmpty(ProjectId, "project-id");

        // The six Hard Rule 10 labels: required, and required to already
        // be values Compute Engine itself will accept as a label.
        RequireLabel(InstallationId, "installation-id");
        RequireLabel(JobId, "job-id");
        RequireLabel(Model, "model");
        RequireLabel(Lifecycle, "lifecycle");
        // AppVersion is exempt from RequireLabel: it is commonly a semantic
        // version ("0.1.0") containing dots, which Compute Engine's label
        // charset forbids. ToLabels() below sanitizes it into a compliant
        // value instead of rejecting a perfectly normal app version - see
        // that method's own comment for why this one field is treated
        // differently from the other five.
        RequireNonEmpty(AppVersion, "app-version");

        RequireNonEmpty(TerminationAction, "instanceTerminationAction");

        if (MaxRunDuration <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("VmSpec is missing a positive maxRunDuration (Hard Rule 10).");
        }

        if (VmName.Length > 63 || !GcpResourceNamePattern.IsMatch(VmName))
        {
            throw new InvalidOperationException(
                $"VmSpec's computed VM name '{VmName}' (from job-id '{JobId}') is not a valid Compute Engine resource " +
                "name: it must start with a lowercase letter, end with a lowercase letter or digit, contain only " +
                "lowercase letters, digits and hyphens, and be at most 63 characters (Hard Rule 9).");
        }
    }

    /// <summary>
    /// The six standard labels (Hard Rule 10), keyed exactly as the Cloud
    /// page discovers them by, and sanitized where a field is allowed to
    /// contain characters Compute Engine's label charset forbids.
    /// </summary>
    public IReadOnlyDictionary<string, string> ToLabels()
    {
        EnsurePreconditions();
        return new Dictionary<string, string>
        {
            ["app"] = AppLabelValue,
            ["installation-id"] = InstallationId,
            ["job-id"] = JobId,
            ["model"] = Model,
            ["app-version"] = SanitizeForLabel(AppVersion),
            ["lifecycle"] = Lifecycle,
        };
    }

    private static void RequireNonEmpty(string value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"VmSpec is missing required field '{fieldName}'.");
        }
    }

    private static void RequireLabel(string value, string labelName)
    {
        RequireNonEmpty(value, labelName);

        if (!GcpLabelValuePattern.IsMatch(value))
        {
            throw new InvalidOperationException(
                $"VmSpec is missing required label '{labelName}' (Hard Rule 10), or its value '{value}' is not a " +
                "valid Compute Engine label value: it must be 1-63 lowercase letters, digits, underscores or " +
                "hyphens, with no dots, spaces or uppercase letters.");
        }
    }

    /// <summary>
    /// Best-effort conversion of a human-facing value (a semantic version
    /// like "0.1.0") into a Compute Engine label value: lowercased, every
    /// disallowed character replaced with a hyphen, truncated to 63 chars.
    /// A 1:1 character replacement can never turn a non-empty string into
    /// an empty one, and <see cref="RequireNonEmpty"/> already rejected an
    /// empty/whitespace-only <paramref name="raw"/> before this runs, so
    /// the result is always a valid label value by construction - there is
    /// no "sanitized but still invalid" case to guard against here, unlike
    /// <see cref="RequireLabel"/> for the other five fields, which are
    /// never silently rewritten.
    /// </summary>
    private static string SanitizeForLabel(string raw)
    {
        var candidate = Regex.Replace(raw.Trim().ToLowerInvariant(), "[^a-z0-9_-]", "-");
        return candidate.Length > 63 ? candidate[..63] : candidate;
    }
}

public sealed record VmDescriptor(string Name, string Zone, string Status, string? StatusReason = null);
