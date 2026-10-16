using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.LocalEngine;
using DnaEntropyGraph.LocalEngine;

namespace DnaEntropyGraph.App;

/// <summary>
/// Where the local engine's files are: the engine folder under the app data folder, and the three things that ship beside the app
/// (<c>engine\uv.exe</c>, the pinned package list and the worker wheel, issue #172). The two development variables let a developer point the
/// engine at an existing Python (and a worker source folder) instead of installing one; a PC with no NVIDIA GPU can then still run the
/// mock predictor through the whole This PC path.
/// </summary>
public static class LocalEngineSetup
{
    public const string PythonVariable = "DEG_LOCAL_ENGINE_PYTHON";

    public const string WorkerSourceVariable = "DEG_LOCAL_ENGINE_WORKER_SRC";

    public const string LockFileName = "requirements-win-cu12x.lock";

    public static string ShippedFolder => Path.Combine(AppContext.BaseDirectory, "engine");

    public static LocalEngineLayout Layout(AppDataRoot root)
    {
        var python = Environment.GetEnvironmentVariable(PythonVariable);
        var source = Environment.GetEnvironmentVariable(WorkerSourceVariable);
        return new LocalEngineLayout(
            root.EngineDirectory,
            string.IsNullOrWhiteSpace(python) ? null : python,
            string.IsNullOrWhiteSpace(source) ? null : new Dictionary<string, string> { ["PYTHONPATH"] = source });
    }

    public static ILocalEngineInstaller Installer(IProcessLauncher launcher, LocalEngineLayout layout, AppDataRoot root, LocalEngineManager manager)
    {
        var shipped = ShippedFolder;
        var wheel = Directory.Exists(shipped)
            ? Directory.EnumerateFiles(shipped, "dna_entropy-*.whl").OrderBy(f => f, StringComparer.Ordinal).LastOrDefault()
            : null;
        return new LocalEngineInstaller(
            launcher,
            layout,
            Path.Combine(shipped, "uv.exe"),
            Path.Combine(shipped, LockFileName),
            wheel ?? Path.Combine(shipped, "dna_entropy.whl"),
            root.Path,
            manager.CheckHealthAsync);
    }
}
