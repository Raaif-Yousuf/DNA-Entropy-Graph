namespace DnaEntropyGraph.Core.Viewers;

/// <summary>
/// Builds the lines IGV's batch port understands (issue #586): one command per line, paths in double quotes so a space
/// does not split them. A path with a line break would start a second command, so it is refused.
/// </summary>
public static class IgvBatchCommands
{
    /// <summary>IGV's default batch port; the user can change it in IGV's preferences and in this app's settings.</summary>
    public const int DefaultPort = 60151;

    public static IReadOnlyList<string> Build(IgvFiles files)
    {
        var commands = new List<string> { "new" };
        if (files.Genome is not null)
        {
            commands.Add("genome " + Quote(files.Genome));
        }

        commands.AddRange(files.BedGraphs.Select(p => "load " + Quote(p)));
        commands.AddRange(files.Gff3s.Select(p => "load " + Quote(p)));
        return commands;
    }

    /// <summary>
    /// Arguments for starting IGV when it is not running: the comma-joined track list, then <c>-g</c> and the genome. IGV splits
    /// its file argument on commas, so a track whose path has one is left out (the genome and the other tracks still load).
    /// </summary>
    public static IReadOnlyList<string> LaunchArguments(IgvFiles files)
    {
        var arguments = new List<string>();
        var tracks = files.BedGraphs.Concat(files.Gff3s).Where(p => !p.Contains(',')).ToList();
        if (tracks.Count > 0)
        {
            arguments.Add(string.Join(',', tracks));
        }

        if (files.Genome is not null)
        {
            arguments.Add("-g");
            arguments.Add(files.Genome);
        }

        return arguments;
    }

    private static string Quote(string path)
    {
        if (path.Contains('\n') || path.Contains('\r') || path.Contains('"'))
        {
            throw new ArgumentException("A path with a line break or a quote cannot be sent to IGV.", nameof(path));
        }

        return "\"" + path + "\"";
    }
}
