namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// A persistent disk as a gateway reports it (issue #105, the Cloud page). <paramref name="SizeGb"/> is what the disk bills for;
/// <paramref name="Status"/> is Compute's own (<c>READY</c>, <c>CREATING</c>, <c>FAILED</c>...). <paramref name="AttachedTo"/> names the
/// VMs that hold it (empty or null: detached), so the cleanup can tell a leftover from a disk in use. A disk a gateway lists always
/// carries the app label; one without it is never listed (Hard Rules 9 and 10).
/// </summary>
public sealed record DiskDescriptor(
    string Name,
    string Zone,
    int SizeGb,
    string Status,
    IReadOnlyDictionary<string, string> Labels,
    DateTimeOffset? CreatedAt = null,
    IReadOnlyList<string>? AttachedTo = null)
{
    /// <summary>The VMs holding this disk, never null.</summary>
    public IReadOnlyList<string> Users => AttachedTo ?? [];

    /// <summary>True when the labels carry <c>app=dna-entropy-graph</c> and <c>installation-id=<paramref name="installationId"/></c>.</summary>
    public bool BelongsTo(string installationId)
        => Labels.TryGetValue("app", out var app) && app == VmSpec.AppLabelValue
            && Labels.TryGetValue("installation-id", out var owner) && owner == installationId;
}

/// <summary>How many objects, and how many bytes, sit under one bucket prefix.</summary>
public sealed record PrefixUsage(int ObjectCount, long TotalBytes);

/// <summary>The rule for a prefix a delete may be pointed at: a whole, slash-terminated path below the bucket root, so a typo cannot reach the whole bucket.</summary>
public static class StoragePrefixes
{
    /// <summary>Throws <see cref="ArgumentException"/> for an empty prefix, the root, one not ending in <c>/</c>, an absolute one, or one with a <c>..</c> segment.</summary>
    public static void RequireDeletable(string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix)
            || prefix.StartsWith('/')
            || !prefix.EndsWith('/')
            || prefix.Split('/').Any(segment => segment == ".."))
        {
            throw new ArgumentException($"'{prefix}' is not a prefix a delete may use: it must be a folder path such as jobs/<id>/ (never the bucket root).", nameof(prefix));
        }
    }
}
