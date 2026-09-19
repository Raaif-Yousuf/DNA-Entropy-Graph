namespace DnaEntropyGraph.Persistence.Tests;

/// <summary>
/// A fresh, isolated directory per test under the OS temp folder (never
/// under the repo - nothing here is subject to the repo's no-recursive-delete
/// hook, which only guards paths inside the checkout). Each test gets its
/// own directory so xUnit's parallel test execution never has two tests
/// racing on the same SQLite/JSON file.
/// </summary>
internal sealed class TempPaths : IDisposable
{
    public string Directory { get; }

    public TempPaths()
    {
        Directory = Path.Combine(Path.GetTempPath(), "deg-persistence-tests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
    }

    public string DatabasePath => Path.Combine(Directory, "app.db");

    public string SettingsPath => Path.Combine(Directory, "settings.json");

    public void Dispose()
    {
        try
        {
            if (System.IO.Directory.Exists(Directory))
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort - a held file handle on Windows must never fail a test's cleanup.
        }
    }
}
