using DnaEntropyGraph.Core.LocalEngine;

namespace DnaEntropyGraph.LocalEngine;

/// <summary>
/// Issue #172: one-click install of the local engine with the bundled <c>uv</c>. The steps, in order:
/// a managed Python 3.12 virtual environment, <c>uv pip sync --require-hashes</c> of the pinned list that ships with the app, the
/// worker package (the same version as the app), then the same health check as the health card. Everything <c>uv</c> writes (the
/// Python it downloads, its cache, the venv) goes under <see cref="EngineFolder"/>, so Remove is deleting one folder and nothing else.
/// <para>
/// THEORY (unverified): the pinned list (<c>requirements-win-cu12x.lock</c>: torch cu128, the Windows flash-attn wheel, vtx, evo2) installs on
/// Windows 11 and passes the health check. See docs/research/2026-10-15-native-windows-evo2.md; the real install is a ToTest row
/// that needs an NVIDIA PC. WSL2 mode (the issue's last checkbox) is not built: it is only needed if that measurement fails, and
/// it would be a second <see cref="ILocalEngineInstaller"/>.
/// </para>
/// </summary>
public sealed class LocalEngineInstaller : ILocalEngineInstaller
{
    private readonly IProcessLauncher _launcher;
    private readonly LocalEngineLayout _layout;
    private readonly string _uvPath;
    private readonly string _lockFile;
    private readonly string _workerPackage;
    private readonly string _appDataFolder;
    private readonly Func<CancellationToken, Task<LocalEngineHealth>> _healthCheck;

    /// <param name="workerPackage">The worker wheel that ships with the app.</param>
    /// <param name="appDataFolder">The folder the engine folder must be strictly inside for Remove to delete it.</param>
    /// <param name="healthCheck">The same check the health card runs, run on the fresh environment.</param>
    public LocalEngineInstaller(
        IProcessLauncher launcher,
        LocalEngineLayout layout,
        string uvPath,
        string lockFile,
        string workerPackage,
        string appDataFolder,
        Func<CancellationToken, Task<LocalEngineHealth>> healthCheck)
    {
        _launcher = launcher;
        _layout = layout;
        _uvPath = uvPath;
        _lockFile = lockFile;
        _workerPackage = workerPackage;
        _appDataFolder = appDataFolder;
        _healthCheck = healthCheck;
    }

    public string EngineFolder => _layout.EngineFolder;

    public Task<LocalInstallResult> InstallAsync(IProgress<LocalInstallProgress>? progress, CancellationToken cancellationToken)
        => RunAsync(createVenv: true, progress, cancellationToken);

    public Task<LocalInstallResult> RepairAsync(IProgress<LocalInstallProgress>? progress, CancellationToken cancellationToken)
        => RunAsync(createVenv: !File.Exists(_layout.PythonExe), progress, cancellationToken);

    public Task<LocalInstallResult> RemoveAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var folder = Path.GetFullPath(_layout.EngineFolder);
        var allowed = Path.GetFullPath(_appDataFolder);
        if (!IsStrictlyInside(folder, allowed))
        {
            return Task.FromResult(new LocalInstallResult(false, LocalEngineErrorCodes.RemoveRefused, "The engine folder is not inside the app data folder."));
        }

        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }

            return Task.FromResult(new LocalInstallResult(true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(new LocalInstallResult(false, LocalEngineErrorCodes.InstallFailed, "The engine folder could not be deleted: " + ex.GetType().Name + "."));
        }
    }

    private static bool IsStrictlyInside(string path, string root)
    {
        var p = Path.TrimEndingDirectorySeparator(path);
        var r = Path.TrimEndingDirectorySeparator(root);
        return p.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<LocalInstallResult> RunAsync(bool createVenv, IProgress<LocalInstallProgress>? progress, CancellationToken cancellationToken)
    {
        if (!File.Exists(_uvPath))
        {
            return new LocalInstallResult(false, LocalEngineErrorCodes.UvMissing, "uv was not found next to the app.");
        }

        if (!File.Exists(_lockFile) || string.IsNullOrWhiteSpace(File.ReadAllText(_lockFile)))
        {
            return new LocalInstallResult(false, LocalEngineErrorCodes.LockMissing, "The pinned package list is missing or empty.");
        }

        if (!File.Exists(_workerPackage))
        {
            return new LocalInstallResult(false, LocalEngineErrorCodes.LockMissing, "The worker package that ships with the app is missing.");
        }

        var python = _layout.PythonExe;
        var steps = new List<(LocalInstallStep Step, string[] Arguments)>();
        if (createVenv)
        {
            steps.Add((LocalInstallStep.CreatingEnvironment, ["venv", "--python", "3.12", "--managed-python", _layout.VenvFolder]));
        }

        steps.Add((LocalInstallStep.InstallingPackages, ["pip", "sync", "--python", python, "--require-hashes", _lockFile]));
        steps.Add((LocalInstallStep.InstallingWorker, ["pip", "install", "--python", python, "--no-deps", _workerPackage]));

        var environment = new Dictionary<string, string>
        {
            ["UV_PYTHON_INSTALL_DIR"] = Path.Combine(EngineFolder, "python"),
            ["UV_CACHE_DIR"] = Path.Combine(EngineFolder, "uv-cache"),
            ["UV_NO_PROGRESS"] = "1",
        };

        try
        {
            Directory.CreateDirectory(EngineFolder);
            foreach (var (step, arguments) in steps)
            {
                progress?.Report(new LocalInstallProgress(step));
                var output = await _launcher
                    .RunToEndAsync(new ProcessSpec(_uvPath, arguments, EngineFolder, environment), cancellationToken)
                    .ConfigureAwait(false);
                if (!output.Started || output.ExitCode != 0)
                {
                    // Only the step and the exit code are kept: uv's text can carry a user name in a path.
                    return new LocalInstallResult(false, LocalEngineErrorCodes.InstallFailed, $"{step} failed (exit code {output.ExitCode}).");
                }

                ReportLastLine(progress, step, output.Text);
            }

            progress?.Report(new LocalInstallProgress(LocalInstallStep.Checking));
            var health = await _healthCheck(cancellationToken).ConfigureAwait(false);
            return health.State == LocalEngineState.Healthy
                ? new LocalInstallResult(true)
                : new LocalInstallResult(false, LocalEngineErrorCodes.CheckFailed, "The new engine did not pass the health check: " + health.State + ".");
        }
        catch (OperationCanceledException)
        {
            return new LocalInstallResult(false, LocalEngineErrorCodes.Cancelled);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new LocalInstallResult(false, LocalEngineErrorCodes.InstallFailed, "The engine folder could not be written: " + ex.GetType().Name + ".");
        }
    }

    private static void ReportLastLine(IProgress<LocalInstallProgress>? progress, LocalInstallStep step, string text)
    {
        var last = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        if (last is not null)
        {
            progress?.Report(new LocalInstallProgress(step, last.Length > 200 ? last[..200] : last));
        }
    }
}
