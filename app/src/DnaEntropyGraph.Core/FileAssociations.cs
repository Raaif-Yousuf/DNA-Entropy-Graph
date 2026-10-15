namespace DnaEntropyGraph.Core;

/// <summary>The sequence file types the app offers itself for in "Open with" (issue #108), and the one program id it registers under.</summary>
public static class FileAssociations
{
    public const string ProgId = "DnaEntropyGraph.Sequence";

    /// <summary>GenBank and FASTA files. Never claimed as the default: the entry only appears in Explorer's Open with list.</summary>
    public static IReadOnlyList<string> Extensions { get; } = [".gb", ".gbk", ".genbank", ".fasta", ".fa", ".fna"];

    public static bool IsSequenceFile(string path)
        => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
}

/// <summary>The current user's <c>HKEY_CURRENT_USER</c> keys, as the file-association writer needs them (the App project implements it over the Windows registry; tests over a dictionary).</summary>
public interface IClassesRegistry
{
    void SetValue(string subKeyPath, string? valueName, string value);

    void DeleteSubKeyTree(string subKeyPath);

    void DeleteValue(string subKeyPath, string valueName);

    bool ValueExists(string subKeyPath, string? valueName);
}

/// <summary>
/// Adds or removes the "Open with" entries (issue #108): a program id whose open command is this exe, listed under each sequence
/// extension's <c>OpenWithProgids</c>. It writes under the current user only and never sets an extension's default value, so no
/// file type is taken from the program the user already uses (Hard Rule 14 spirit: nothing of the user's is changed).
/// </summary>
public sealed class FileAssociationService(IClassesRegistry registry, Func<string?> programPath)
{
    private static readonly string ProgIdKey = $"Software\\Classes\\{FileAssociations.ProgId}";

    private static readonly string CommandKey = ProgIdKey + "\\shell\\open\\command";

    public bool IsEnabled => registry.ValueExists(CommandKey, null);

    public void Enable()
    {
        var exe = programPath();
        if (string.IsNullOrWhiteSpace(exe) || exe.Contains('"', StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The program path is not known, so there is nothing to register.");
        }

        registry.SetValue(ProgIdKey, null, "DNA Entropy Graph");
        registry.SetValue(ProgIdKey, "FriendlyTypeName", "DNA sequence file");
        registry.SetValue(CommandKey, null, $"\"{exe}\" \"%1\"");
        foreach (var extension in FileAssociations.Extensions)
        {
            registry.SetValue($"Software\\Classes\\{extension}\\OpenWithProgids", FileAssociations.ProgId, string.Empty);
        }
    }

    public void Disable()
    {
        foreach (var extension in FileAssociations.Extensions)
        {
            registry.DeleteValue($"Software\\Classes\\{extension}\\OpenWithProgids", FileAssociations.ProgId);
        }

        registry.DeleteSubKeyTree(ProgIdKey);
    }
}
