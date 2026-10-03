using System.Text.Json;

namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// The list of worker images pinned by digest that ships with the app
/// (<c>worker-images.json</c>: <c>{"images":[{"version","cuda","cpu"}]}</c>).
/// It is also the allowlist: a reference not in it is refused unless developer
/// mode is on. An entry that is not pinned by digest is dropped (and counted in
/// <see cref="DroppedCount"/>) so it can never reach a VM; an unreadable file
/// throws rather than becoming an empty allowlist that hides the problem.
/// </summary>
public sealed class PinnedWorkerImageList
{
    private readonly List<(string Version, string? Cuda, string? Cpu)> _entries;

    private PinnedWorkerImageList(List<(string Version, string? Cuda, string? Cpu)> entries, int dropped)
    {
        _entries = entries;
        DroppedCount = dropped;
    }

    /// <summary>Entries (or half-entries) discarded because their reference is not pinned by digest.</summary>
    public int DroppedCount { get; }

    public bool IsEmpty => _entries.Count == 0;

    public static PinnedWorkerImageList Parse(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The pinned worker image list is not valid JSON.", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("images", out var images)
                || images.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("The pinned worker image list has no images array.");
            }

            var entries = new List<(string Version, string? Cuda, string? Cpu)>();
            var dropped = 0;
            foreach (var image in images.EnumerateArray())
            {
                // A typo'd key ("cud") or a non-object entry must be visible, not an empty allowlist:
                // anything outside the three known keys, or an entry with no image, counts as dropped
                // (counted, not thrown, so one bad entry cannot hide the good ones, and the shipped-list
                // guard fails on DroppedCount > 0).
                if (image.ValueKind != JsonValueKind.Object || HasUnknownKey(image))
                {
                    dropped++;
                    continue;
                }

                var version = Text(image, "version");
                if (version is null)
                {
                    dropped++;
                    continue;
                }

                var cuda = Pinned(Text(image, "cuda"), ref dropped);
                var cpu = Pinned(Text(image, "cpu"), ref dropped);
                if (cuda is not null || cpu is not null)
                {
                    entries.Add((version, cuda, cpu));
                }
                else if (Text(image, "cuda") is null && Text(image, "cpu") is null)
                {
                    dropped++;
                }
            }

            return new PinnedWorkerImageList(entries, dropped);
        }
    }

    /// <summary>The reference pinned for <paramref name="appVersion"/>, or null. Never another version's build.</summary>
    public string? Find(string appVersion, bool gpu)
    {
        foreach (var (version, cuda, cpu) in _entries)
        {
            if (string.Equals(version, appVersion, StringComparison.Ordinal))
            {
                return gpu ? cuda : cpu;
            }
        }

        return null;
    }

    /// <summary>
    /// True only when <paramref name="reference"/> is exactly the image pinned for
    /// <paramref name="appVersion"/> and the variant (-cuda when <paramref name="gpu"/>, else -cpu).
    /// A pinned image of another version or the other variant is not allowed: a -cpu image on a GPU
    /// VM boots with no torch.
    /// </summary>
    public bool Allows(string reference, string appVersion, bool gpu)
        => string.Equals(Find(appVersion, gpu), reference, StringComparison.Ordinal);

    private static bool HasUnknownKey(JsonElement entry)
        => entry.EnumerateObject().Any(p => p.Name is not ("version" or "cuda" or "cpu"));

    private static string? Text(JsonElement owner, string name)
        => owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? Pinned(string? reference, ref int dropped)
    {
        if (reference is null)
        {
            return null;
        }

        if (StartupMetadata.IsPinnedImageReference(reference))
        {
            return reference;
        }

        dropped++;
        return null;
    }
}
