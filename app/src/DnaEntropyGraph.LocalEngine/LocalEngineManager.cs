using System.Text.Json;
using DnaEntropyGraph.Core.LocalEngine;

namespace DnaEntropyGraph.LocalEngine;

/// <summary>Where everything in the engine folder is (issue #172): one venv, nothing else, so Remove is one folder.</summary>
public sealed class LocalEngineLayout
{
    private readonly string? _pythonOverride;

    /// <param name="pythonOverride">A python to use instead of the engine's own venv (development and the real-worker test only).</param>
    /// <param name="environment">Variables added to every program started in the engine (for example <c>PYTHONPATH</c> in a development checkout).</param>
    public LocalEngineLayout(string engineFolder, string? pythonOverride = null, IReadOnlyDictionary<string, string>? environment = null)
    {
        EngineFolder = engineFolder;
        _pythonOverride = pythonOverride;
        Environment = environment;
    }

    public string EngineFolder { get; }

    public IReadOnlyDictionary<string, string>? Environment { get; }

    /// <summary>Inherited variables that point Python somewhere else than the engine's own venv; a child must not see them (a user's <c>PYTHONPATH</c> or an activated venv would shadow the engine's packages).</summary>
    public static IReadOnlyList<string> InheritedPythonVariables { get; } = ["PYTHONPATH", "PYTHONHOME", "VIRTUAL_ENV"];

    /// <summary>What to remove from a child's environment: <see cref="InheritedPythonVariables"/> unless the development override in <see cref="Environment"/> sets it.</summary>
    public IReadOnlyList<string> ClearedVariables => InheritedPythonVariables.Where(name => Environment is null || !Environment.ContainsKey(name)).ToList();

    public string VenvFolder => Path.Combine(EngineFolder, ".venv");

    public string PythonExe => _pythonOverride ?? Path.Combine(VenvFolder, "Scripts", "python.exe");
}

/// <summary>
/// Issues #120 and #173: is this PC able to run the worker, and is the engine in its folder alive. The engine is "alive" when the
/// worker inside its own Python answers <c>health</c> with the app's own version: that one question covers "the venv exists, the
/// package imports and the contract matches". It is checked by running the engine, never by looking at files.
/// </summary>
public sealed class LocalEngineManager : ILocalReadinessSource
{
    private static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(60);

    private readonly IGpuDetector _gpu;
    private readonly IFreeDiskSpace _disk;
    private readonly IProcessLauncher _launcher;
    private readonly LocalEngineLayout _layout;
    private readonly string _appVersion;
    private readonly Func<string, bool> _fileExists;

    public LocalEngineManager(
        IGpuDetector gpu,
        IFreeDiskSpace disk,
        IProcessLauncher launcher,
        LocalEngineLayout layout,
        string appVersion,
        Func<string, bool>? fileExists = null)
    {
        _gpu = gpu;
        _disk = disk;
        _launcher = launcher;
        _layout = layout;
        _appVersion = appVersion;
        _fileExists = fileExists ?? File.Exists;
    }

    public async Task<LocalReadiness> CheckAsync(CancellationToken cancellationToken)
    {
        var detection = await _gpu.DetectAsync(cancellationToken).ConfigureAwait(false);
        var capability = LocalCapabilityEvaluator.Evaluate(detection, _disk.FreeBytes(_layout.EngineFolder));
        var engine = await CheckHealthAsync(cancellationToken).ConfigureAwait(false);
        return new LocalReadiness(capability, engine);
    }

    public async Task<LocalEngineHealth> CheckHealthAsync(CancellationToken cancellationToken)
    {
        if (!_fileExists(_layout.PythonExe))
        {
            return LocalEngineHealth.NotInstalled("no python in the engine folder");
        }

        var spec = new ProcessSpec(_layout.PythonExe, ["-m", "dna_entropy.worker.cli", "health"], Environment: _layout.Environment, ClearEnvironment: _layout.ClearedVariables);
        var output = await _launcher.RunToEndAsync(spec, cancellationToken, HealthTimeout).ConfigureAwait(false);
        if (!output.Started)
        {
            return Broken("python could not start");
        }

        if (output.ExitCode != 0)
        {
            return Broken("health exited " + output.ExitCode);
        }

        if (!TryReadAnswer(output.Text, out var version, out var evo, out var cuda))
        {
            return Broken("health printed no usable line");
        }

        var state = string.Equals(version, _appVersion, StringComparison.Ordinal) ? LocalEngineState.Healthy : LocalEngineState.WorkerVersionMismatch;
        return new LocalEngineHealth(state, version, evo, cuda, "worker " + version);
    }

    private static LocalEngineHealth Broken(string detail) => new(LocalEngineState.Broken, null, false, false, detail);

    private static bool TryReadAnswer(string text, out string version, out bool evo, out bool cuda)
    {
        version = string.Empty;
        evo = false;
        cuda = false;
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Reverse())
        {
            if (!line.StartsWith('{'))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("worker", out var worker)
                    || worker.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                version = worker.GetString() ?? string.Empty;
                evo = root.TryGetProperty("evo", out var e) && e.ValueKind == JsonValueKind.True;
                cuda = root.TryGetProperty("cuda", out var c) && c.ValueKind == JsonValueKind.True;
                return version.Length > 0;
            }
            catch (JsonException)
            {
                // Not the line we want; keep looking.
            }
        }

        return false;
    }
}
