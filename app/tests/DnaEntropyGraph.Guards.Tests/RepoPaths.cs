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

    /// <summary>Every real, source-controlled .cs file under src/ - generated obj/bin output excluded, since a scan of generated code is not a scan of anything anyone wrote.</summary>
    public static string[] AllCSharpFiles => Directory.GetFiles(Path.Combine(AppRoot, "src"), "*.cs", SearchOption.AllDirectories)
        .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
            && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
        .ToArray();

    // MEASURED 2026-10-02: under `dotnet test --artifacts-path <dir outside the repo>` (the per-lane
    // convention for concurrent agents) the output directory is not under app/, so walking up from it
    // found nothing and 9 guards threw. The compile-time path of this source file is the fallback.
    private static string FindAppRoot([System.Runtime.CompilerServices.CallerFilePath] string thisFile = "")
        => FindSlnAbove(AppContext.BaseDirectory) ?? FindSlnAbove(Path.GetDirectoryName(thisFile))
        ?? throw new InvalidOperationException(
            $"Could not find DnaEntropyGraph.sln by walking up from {AppContext.BaseDirectory} or {thisFile}. " +
            "A guard that cannot find the app/ tree would silently scan nothing and pass for the wrong reason.");

    private static string? FindSlnAbove(string? start)
    {
        var dir = string.IsNullOrEmpty(start) ? null : new DirectoryInfo(start);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DnaEntropyGraph.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }
}
