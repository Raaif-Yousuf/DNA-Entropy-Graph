namespace DnaEntropyGraph.Core.Inputs;

/// <summary>What a dropped folder contributes (issue #63): the sequence files directly inside it, in name order.</summary>
public static class InputFolderScanner
{
    /// <summary>Files with a FASTA or GenBank extension in <paramref name="folder"/> itself (sub-folders are not followed). Empty when the folder is missing or unreadable.</summary>
    public static IReadOnlyList<string> SequenceFiles(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder)
                .Where(path => SequenceSniffer.DetectKindByExtension(path) is not null)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }
}
