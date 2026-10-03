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
}

/// <summary>An override folder (issue #638) that cannot be used. The app shows <see cref="Path"/> and one action; it never falls back to the real folder.</summary>
public sealed class DataFolderUnusableException(DataFolderProblem reason, string path, Exception? inner = null)
    : Exception($"data folder unusable: {reason} ({path})", inner)
{
    public DataFolderProblem Reason { get; } = reason;

    public string Path { get; } = path;
}

/// <summary>
/// The one folder all app state lives under (issue #638). Resolved once at start-up, in this order: the
/// <c>--profile &lt;dir&gt;</c> (or <c>--profile=&lt;dir&gt;</c>) switch, then the <c>DEG_DATA_DIR</c> environment
/// variable, then <c>%LOCALAPPDATA%\DNAEntropyGraph</c>. The command line wins when both are set. A relative path
/// resolves against the current directory; a missing override folder is created. Everything else (settings, the
/// installation id, the database, auth, logs, run inputs, the WebView2 profile, the diagnostics source) is derived
/// from this value, and nothing else in the app reads the Windows local-data folder
/// (Guards.Tests/DataFolderOverrideTests enforces both).
/// </summary>
public sealed class AppDataRoot
{
    public const string EnvironmentVariable = "DEG_DATA_DIR";

    public const string SwitchName = "--profile";

    private AppDataRoot(string path, bool isOverride)
    {
        Path = path;
        IsOverride = isOverride;
    }

    /// <summary>The absolute root folder.</summary>
    public string Path { get; }

    /// <summary>True when this is not the real per-user folder, so the window says so.</summary>
    public bool IsOverride { get; }

    public string SettingsFile => Combine("settings.json");

    public string InstallationIdFile => Combine("installation_id");

    public string DatabaseFile => Combine("app.db");

    public string AuthDirectory => Combine("auth");

    public string LogsDirectory => Combine("logs");

    /// <summary>Local run folders; each run's saved input copy lives under <c>runs\&lt;jobId&gt;\input</c>.</summary>
    public string RunsDirectory => Combine("runs");

    public string WebView2Directory => Combine("webview2");

    /// <summary>The real per-user folder. Creates nothing.</summary>
    public static AppDataRoot Default() => new(
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DNAEntropyGraph"),
        isOverride: false);

    /// <summary>A root at an explicit folder, as a sandbox. Creates nothing; tests use it with a temp folder.</summary>
    public static AppDataRoot FromPath(string path) => new(System.IO.Path.GetFullPath(path), isOverride: true);

    /// <summary>Resolves the root from the command-line arguments, then the environment, then the default; creates an override folder.</summary>
    /// <exception cref="DataFolderUnusableException">The override is empty, a file, or cannot be created.</exception>
    public static AppDataRoot Resolve(IReadOnlyList<string> args, Func<string, string?> getEnvironmentVariable, string currentDirectory)
    {
        var requested = FromArguments(args) ?? FromEnvironment(getEnvironmentVariable);
        if (requested is null)
        {
            return Default();
        }

        var full = System.IO.Path.GetFullPath(requested, currentDirectory);
        if (File.Exists(full))
        {
            throw new DataFolderUnusableException(DataFolderProblem.NotAFolder, full);
        }

        try
        {
            Directory.CreateDirectory(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new DataFolderUnusableException(DataFolderProblem.CannotCreate, full, ex);
        }

        // A profile that points at the real folder is the real folder: no banner for it.
        var isOverride = !string.Equals(
            System.IO.Path.TrimEndingDirectorySeparator(full),
            Default().Path,
            StringComparison.OrdinalIgnoreCase);
        return new AppDataRoot(full, isOverride);
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
                return i + 1 < args.Count && !string.IsNullOrWhiteSpace(args[i + 1])
                    ? args[i + 1]
                    : throw new DataFolderUnusableException(DataFolderProblem.MissingValue, string.Empty);
            }
        }

        return null;
    }

    private static string? FromEnvironment(Func<string, string?> getEnvironmentVariable)
        => getEnvironmentVariable(EnvironmentVariable) is { } value && !string.IsNullOrWhiteSpace(value) ? value : null;

    private string Combine(string name) => System.IO.Path.Combine(Path, name);
}
