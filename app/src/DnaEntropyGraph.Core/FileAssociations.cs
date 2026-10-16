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

/// <summary>The current user's <c>HKEY_CURRENT_USER</c> keys, as the file-association writer needs them (the App project implements it over the Windows registry; tests over a model with real key semantics).</summary>
public interface IClassesRegistry
{
    void SetValue(string subKeyPath, string? valueName, string value);

    void DeleteSubKeyTree(string subKeyPath);

    void DeleteValue(string subKeyPath, string valueName);

    bool ValueExists(string subKeyPath, string? valueName);

    bool KeyExists(string subKeyPath);

    /// <summary>Deletes the key only if it has no values and no sub keys. True when it was deleted; false when it is missing or not empty.</summary>
    bool DeleteSubKeyIfEmpty(string subKeyPath);
}

/// <summary>Why <see cref="FileAssociationService"/> would not change anything.</summary>
public enum FileAssociationRefusal
{
    /// <summary>The running program's path is not known.</summary>
    NoProgramPath,

    /// <summary>The running program is not the app's own exe (for example <c>dotnet.exe</c> under <c>dotnet run</c>), so registering it would make Explorer start the wrong program.</summary>
    NotTheAppExecutable,

    /// <summary>The app runs on a sandbox profile (<c>--profile</c>); the entries live in the real user's registry, outside any profile.</summary>
    SandboxProfile,
}

/// <summary>A refusal that is not a failure: nothing was written. The message is for logs; the user sees a string chosen by <see cref="Reason"/>.</summary>
public sealed class FileAssociationRefusedException(FileAssociationRefusal reason)
    : InvalidOperationException($"File association change refused: {reason}.")
{
    public FileAssociationRefusal Reason { get; } = reason;
}

/// <summary>
/// Adds or removes the "Open with" entries (issue #108): a program id whose open command is this exe, listed under each sequence
/// extension's <c>OpenWithProgids</c>. It writes under the current user only and never sets an extension's default value, so no
/// file type is taken from the program the user already uses (Hard Rule 14 spirit: nothing of the user's is changed).
/// Enable is all or nothing (a failure rolls back what was written), Disable removes the keys it created once they are empty, and
/// both are refused on a sandbox profile, whose registry writes would land in the real user's Explorer.
/// </summary>
public sealed class FileAssociationService(IClassesRegistry registry, Func<string?> programPath, Func<bool>? isSandboxProfile = null)
{
    /// <summary>The installed app's exe (Velopack <c>--mainExe</c>, ci-app.yml). Anything else is not registered.</summary>
    public const string ExecutableName = "DnaEntropyGraph.App.exe";

    private static readonly string ProgIdKey = $"Software\\Classes\\{FileAssociations.ProgId}";

    private static readonly string CommandKey = ProgIdKey + "\\shell\\open\\command";

    private static string OpenWithKey(string extension) => $"Software\\Classes\\{extension}\\OpenWithProgids";

    /// <summary>True only when the whole registration is there: the open command and an entry under every extension.</summary>
    public bool IsEnabled => registry.ValueExists(CommandKey, null)
        && FileAssociations.Extensions.All(extension => registry.ValueExists(OpenWithKey(extension), FileAssociations.ProgId));

    public void Enable()
    {
        RefuseInSandbox();
        var exe = programPath();
        if (string.IsNullOrWhiteSpace(exe) || exe.Contains('"', StringComparison.Ordinal))
        {
            throw new FileAssociationRefusedException(FileAssociationRefusal.NoProgramPath);
        }

        if (!string.Equals(Path.GetFileName(exe), ExecutableName, StringComparison.OrdinalIgnoreCase))
        {
            throw new FileAssociationRefusedException(FileAssociationRefusal.NotTheAppExecutable);
        }

        try
        {
            registry.SetValue(ProgIdKey, null, "DNA Entropy Graph");
            registry.SetValue(ProgIdKey, "FriendlyTypeName", "DNA sequence file");
            registry.SetValue(CommandKey, null, $"\"{exe}\" \"%1\"");
            foreach (var extension in FileAssociations.Extensions)
            {
                registry.SetValue(OpenWithKey(extension), FileAssociations.ProgId, string.Empty);
            }
        }
        catch
        {
            try
            {
                Remove();
            }
            catch (Exception rollback) when (rollback is not OperationCanceledException)
            {
                System.Diagnostics.Trace.TraceWarning($"file association rollback incomplete: {rollback.GetType().Name}");
            }

            throw;
        }
    }

    public void Disable()
    {
        RefuseInSandbox();
        Remove();
    }

    /// <summary>
    /// Removes the program id and this app's entry under every extension, then the <c>OpenWithProgids</c> and extension keys but only
    /// if they are empty now (another program's entry, or the user's own default, keeps its key). It keeps going after a failure so
    /// one locked key does not leave the rest behind, and rethrows the first failure at the end.
    /// </summary>
    private void Remove()
    {
        Exception? first = null;
        void Step(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                first ??= ex;
            }
        }

        foreach (var extension in FileAssociations.Extensions)
        {
            Step(() => registry.DeleteValue(OpenWithKey(extension), FileAssociations.ProgId));
            Step(() => registry.DeleteSubKeyIfEmpty(OpenWithKey(extension)));
            Step(() => registry.DeleteSubKeyIfEmpty($"Software\\Classes\\{extension}"));
        }

        Step(() => registry.DeleteSubKeyTree(ProgIdKey));
        if (first is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(first).Throw();
        }
    }

    private void RefuseInSandbox()
    {
        if (isSandboxProfile?.Invoke() == true)
        {
            throw new FileAssociationRefusedException(FileAssociationRefusal.SandboxProfile);
        }
    }
}
