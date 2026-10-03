using System.Text.Json;
using System.Text.Json.Serialization;

namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// The one serializer for the <c>OptionsJson</c> column of a run row (issue #59): the engine writes the options when a run
/// starts and the reconciler reads them back to rebuild the request of a run a killed app left behind. Enums go by name, so a
/// reordered enum cannot change what a stored row means.
/// </summary>
public static class RunOptionsJson
{
    private static readonly JsonSerializerOptions Settings = new() { Converters = { new JsonStringEnumConverter() } };

    public static string Serialize(RunOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return JsonSerializer.Serialize(options, Settings);
    }

    /// <summary>The options, or null when the text is not a complete option set (empty, damaged, or missing a required field).</summary>
    public static RunOptions? TryDeserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<RunOptions>(json, Settings);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }
}
