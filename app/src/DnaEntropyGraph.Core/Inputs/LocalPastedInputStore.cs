using System.Text;
using DnaEntropyGraph.Core.Abstractions;

namespace DnaEntropyGraph.Core.Inputs;

/// <summary><see cref="IPastedInputStore"/> on the local disk: <c>&lt;root&gt;\pasted\paste-&lt;time&gt;-&lt;id&gt;.txt</c>, UTF-8 with LF.</summary>
public sealed class LocalPastedInputStore : IPastedInputStore
{
    private readonly string _root;

    /// <param name="root">The app data folder (<c>%LOCALAPPDATA%\DNAEntropyGraph</c> in the app, a temp folder in tests).</param>
    public LocalPastedInputStore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = root;
    }

    public string Save(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var directory = Path.Combine(_root, "pasted");
        Directory.CreateDirectory(directory);
        var name = $"paste-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}.txt";
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'), new UTF8Encoding(false));
        return path;
    }

    public void Delete(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var directory = Path.GetFullPath(Path.Combine(_root, "pasted")) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(directory, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            File.Delete(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort: a leftover pasted copy is harmless
        }
    }
}
