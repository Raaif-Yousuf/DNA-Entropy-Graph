using System.Text.Json;
using System.Text.Json.Serialization;

namespace DnaEntropyGraph.Core.Runs;

/// <summary>
/// The one (de)serialiser for <c>Runs.OptionsJson</c>, so a re-run reads exactly what the run
/// that wrote it wrote (issue #101: "re-run with same settings").
/// </summary>
public static class RunOptionsJson
{
    private static readonly JsonSerializerOptions Options = new() { Converters = { new JsonStringEnumConverter() } };

    public static string Serialize(RunOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return JsonSerializer.Serialize(options, Options);
    }

    /// <summary>The recorded options, or null when the text is empty, not JSON, or lacks a required setting.</summary>
    public static RunOptions? TryDeserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<RunOptions>(json, Options);
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
