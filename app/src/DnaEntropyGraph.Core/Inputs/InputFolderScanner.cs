namespace DnaEntropyGraph.Core.Inputs;

/// <summary>What a dropped folder contributes (issue #63): the sequence files directly inside it, in name order.</summary>
public static class InputFolderScanner
{
    private const int HeadChars = 4096;

    /// <summary>
    /// Files in <paramref name="folder"/> itself (sub-folders are not followed) that the Add files picker would offer:
    /// a FASTA or GenBank extension is taken as is, and a <c>.txt</c> only when its first line is a FASTA or GenBank
    /// header (DECISION, agent-made, reversible: a folder of notes and logs must not become a pile of error pills; a
    /// headerless sequence file is still added with Add files). Empty when the folder is missing or unreadable.
    /// </summary>
    public static IReadOnlyList<string> SequenceFiles(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder)
                .Where(IsWorthAdding)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }

    private static bool IsWorthAdding(string path)
    {
        if (!SequenceFileTypes.IsSequenceFile(path))
        {
            return false;
        }

        return SequenceSniffer.DetectKindByExtension(path) is not null || StartsWithHeader(path);
    }

    private static bool StartsWithHeader(string path)
    {
        try
        {
            using var reader = new StreamReader(path, detectEncodingFromByteOrderMarks: true);
            var buffer = new char[HeadChars];
            var read = reader.Read(buffer, 0, buffer.Length);
            return SequenceSniffer.DetectKindFromContent(new string(buffer, 0, read)) != InputKind.Paste;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
