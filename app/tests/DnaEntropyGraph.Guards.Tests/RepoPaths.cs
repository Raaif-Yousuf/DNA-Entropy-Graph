namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// Locates the real <c>app/</c> tree at test-run time by walking up from the
/// test assembly's own output directory until <c>DnaEntropyGraph.sln</c> is
/// found. Every scanner test below runs against these real, on-disk paths -
/// not a copy, not a fixture that could drift from what actually ships.
/// </summary>
internal static class RepoPaths
{
    public static string AppRoot { get; } = FindAppRoot();

    public static string[] AllCsprojFiles => Directory.GetFiles(AppRoot, "*.csproj", SearchOption.AllDirectories);

    public static string[] AllXamlCsFiles => Directory.GetFiles(Path.Combine(AppRoot, "src"), "*.xaml.cs", SearchOption.AllDirectories);

    public static string[] AllReswFiles => Directory.GetFiles(Path.Combine(AppRoot, "src"), "*.resw", SearchOption.AllDirectories);

    public static string[] AllXamlFiles => Directory.GetFiles(Path.Combine(AppRoot, "src"), "*.xaml", SearchOption.AllDirectories);

    private static string FindAppRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DnaEntropyGraph.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"Could not find DnaEntropyGraph.sln by walking up from {AppContext.BaseDirectory}. " +
            "A guard that cannot find the app/ tree would silently scan nothing and pass for the wrong reason.");
    }
}
