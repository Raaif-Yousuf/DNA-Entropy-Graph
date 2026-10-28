using System.Security;

namespace DnaEntropyGraph.Core;

/// <summary>Why a data folder override could not be used (see <see cref="DataFolderUnusableException"/>).</summary>
public enum DataFolderProblem
{
    /// <summary><c>--profile</c> was given with no folder after it.</summary>
    MissingValue,

    /// <summary>The path exists and is a file.</summary>
    NotAFolder,

    /// <summary>The folder could not be created or is not writable.</summary>
    CannotCreate,

    /// <summary>The value is not a path the system can use (for example an embedded NUL or <c>C:\a:b:c</c>).</summary>
    InvalidPath,
}

/// <summary>An override folder (issue #638) that cannot be used. The app shows <see cref="Path"/> and one action; it never falls back to the real folder.</summary>
public sealed class DataFolderUnusableException(DataFolderProblem reason, string path, Exception? inner = null, bool isPortable = false)
    : Exception($"data folder unusable: {reason} ({path})", inner)
{
    public DataFolderProblem Reason { get; } = reason;

    public string Path { get; } = path;

    /// <summary>True when the folder came from <c>portable.flag</c> rather than <c>--profile</c> or <c>DEG_DATA_DIR</c>, so the message names the portable fix (issue #144).</summary>
    public bool IsPortable { get; } = isPortable;
}

/// <summary>
/// The one folder all app state lives under (issue #638). Resolved once at start-up, in this order: the
/// <c>--profile &lt;dir&gt;</c> (or <c>--profile=&lt;dir&gt;</c>) switch, then the <c>DEG_DATA_DIR</c> environment
/// variable, then a <c>portable.flag</c> beside the program (state in <c>data</c> beside it, issue #144), then <c>%LOCALAPPDATA%\DNAEntropyGraph</c>. The command line wins when both are set. A relative path
/// resolves against the current directory; a missing override folder is created. Everything else (settings, the
/// installation id, the database, auth, logs, run inputs, the WebView2 profile, the diagnostics source) is derived
/// from this value, and nothing else in the app reads the Windows local-data folder
/// (Guards.Tests/DataFolderOverrideTests enforces both).
/// </summary>
public sealed class AppDataRoot
{
    public const string EnvironmentVariable = "DEG_DATA_DIR";

    public const string SwitchName = "--profile";

    /// <summary>An empty file beside the program that switches the app to portable mode (issue #144).</summary>
    public const string PortableFlagFileName = "portable.flag";

    /// <summary>The state folder created beside the program in portable mode.</summary>
    public const string PortableDataFolderName = "data";

    public const string PortableOutputsFolderName = "outputs";

    private AppDataRoot(string path, bool isOverride, string? portableBase = null)
    {
        Path = path;
        IsOverride = isOverride;
        PortableBase = portableBase;
    }

    /// <summary>The absolute root folder.</summary>
    public string Path { get; }

    /// <summary>True when this is not the real per-user folder, so the window says so.</summary>
    public bool IsOverride { get; }

    /// <summary>True when <c>portable.flag</c> sits beside the program and state lives in <c>data</c> beside it (issue #144). Not an override: no sandbox banner.</summary>
    public bool IsPortable => PortableBase is not null;

    /// <summary>The folder that holds <c>portable.flag</c> and <c>data</c> (and <c>outputs</c>): beside the exe, or beside <c>Update.exe</c> in a Velopack install. Null when not portable.</summary>
    public string? PortableBase { get; }

    /// <summary>Where results go by default in portable mode, beside <c>data</c>; null when not portable.</summary>
    public string? PortableOutputsDirectory => PortableBase is null ? null : System.IO.Path.Combine(PortableBase, PortableOutputsFolderName);

    /// <summary>The "where do results go when the user chose nothing" lookup: the portable outputs folder in portable mode, else <paramref name="downloads"/> (issue #144).</summary>
    public Func<string?> DefaultOutputSource(Func<string?> downloads)
    {
        var portable = PortableOutputsDirectory;
        return portable is null ? downloads : () => portable;
    }

    /// <summary>Stores paths under <see cref="PortableBase"/> relative to it, so a changed drive letter does not lose results.</summary>
    public DataPathMapper PathMapper => new(PortableBase);

    public string SettingsFile => Combine("settings.json");

    public string InstallationIdFile => Combine("installation_id");

    public string DatabaseFile => Combine("app.db");

    public string AuthDirectory => Combine("auth");

    public string LogsDirectory => Combine("logs");

    /// <summary>Local run folders; each run's saved input copy lives under <c>runs\&lt;jobId&gt;\input</c>.</summary>
    public string RunsDirectory => Combine("runs");

    /// <summary>The local engine (issue #172): its venv, its managed Python and its package cache. Remove deletes exactly this folder.</summary>
    public string EngineDirectory => Combine("engine");

    public string WebView2Directory => Combine("webview2");

    /// <summary>Generated <c>.fai</c> indexes of long FASTA files the viewer opens (issue #497): app-owned, so nothing is written beside the user's files.</summary>
    public string FastaIndexDirectory => Combine("fai");

    /// <summary>The real per-user folder. Creates nothing.</summary>
    public static AppDataRoot Default() => new(
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DNAEntropyGraph"),
        isOverride: false);

    /// <summary>A root at an explicit folder, as a sandbox. Creates nothing; tests use it with a temp folder.</summary>
    public static AppDataRoot FromPath(string path) => new(System.IO.Path.GetFullPath(path), isOverride: true);

    /// <summary>Resolves the root from the command-line arguments, then the environment, then the default; creates an override folder.</summary>
    /// <param name="writeProbe">Test seam: checks the folder is writable. Production creates and deletes a uniquely named file in it.</param>
    /// <exception cref="DataFolderUnusableException">The override is empty, malformed, a file, not writable, or cannot be created.</exception>
    public static AppDataRoot Resolve(IReadOnlyList<string> args, Func<string, string?> getEnvironmentVariable, string currentDirectory, Action<string>? writeProbe = null, string? programDirectory = null)
    {
        var requested = FromArguments(args) ?? FromEnvironment(getEnvironmentVariable);
        string? portableBase = null;
        if (requested is null && programDirectory is not null)
        {
            var candidate = PortableBaseFor(programDirectory);
            if (File.Exists(System.IO.Path.Combine(candidate, PortableFlagFileName)))
            {
                portableBase = candidate;
                requested = System.IO.Path.Combine(candidate, PortableDataFolderName);
            }
        }

        if (requested is null)
        {
            return Default();
        }

        string full;
        try
        {
            full = System.IO.Path.GetFullPath(requested, currentDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or SecurityException)
        {
            throw new DataFolderUnusableException(DataFolderProblem.InvalidPath, requested.Replace('\0', '?'), ex, portableBase is not null);
        }

        if (File.Exists(full))
        {
            throw new DataFolderUnusableException(DataFolderProblem.NotAFolder, full, isPortable: portableBase is not null);
        }

        try
        {
            Directory.CreateDirectory(full);
            (writeProbe ?? ProbeWritable)(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or SecurityException)
        {
            throw new DataFolderUnusableException(DataFolderProblem.CannotCreate, full, ex, portableBase is not null);
        }

        if (portableBase is not null)
        {
            return new AppDataRoot(full, isOverride: false, portableBase);
        }

        // A profile that points at the real folder is the real folder: no banner for it.
        var isOverride = !string.Equals(
            System.IO.Path.TrimEndingDirectorySeparator(full),
            Default().Path,
            StringComparison.OrdinalIgnoreCase);
        return new AppDataRoot(full, isOverride);
    }

    /// <summary>
    /// The folder the flag and the data live in. A Velopack install runs the exe from <c>&lt;root&gt;\current</c> and an update swaps that folder
    /// for a new one, so state kept inside it would be lost: when the program folder is named <c>current</c> and <c>Update.exe</c> sits beside it,
    /// the parent is the portable base (issue #144, #805).
    /// </summary>
    private static string PortableBaseFor(string programDirectory)
    {
        var dir = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(programDirectory));
        var parent = System.IO.Path.GetDirectoryName(dir);
        return parent is not null
            && string.Equals(System.IO.Path.GetFileName(dir), "current", StringComparison.OrdinalIgnoreCase)
            && File.Exists(System.IO.Path.Combine(parent, "Update.exe"))
            ? parent
            : dir;
    }

    private static string? FromArguments(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg.StartsWith(SwitchName + "=", StringComparison.Ordinal))
            {
                var value = arg[(SwitchName.Length + 1)..];
                return string.IsNullOrWhiteSpace(value)
                    ? throw new DataFolderUnusableException(DataFolderProblem.MissingValue, string.Empty)
                    : value;
            }

            if (arg == SwitchName)
            {
                return i + 1 < args.Count && !string.IsNullOrWhiteSpace(args[i + 1]) && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                    ? args[i + 1]
                    : throw new DataFolderUnusableException(DataFolderProblem.MissingValue, string.Empty);
            }
        }

        return null;
    }

    private static void ProbeWritable(string folder)
    {
        var probe = System.IO.Path.Combine(folder, ".deg-write-probe-" + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(probe, []);
        File.Delete(probe);
    }

    private static string? FromEnvironment(Func<string, string?> getEnvironmentVariable)
        => getEnvironmentVariable(EnvironmentVariable) is { } value && !string.IsNullOrWhiteSpace(value) ? value : null;

    private string Combine(string name) => System.IO.Path.Combine(Path, name);
}
