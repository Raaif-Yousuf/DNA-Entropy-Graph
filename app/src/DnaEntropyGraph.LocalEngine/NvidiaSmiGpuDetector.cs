using DnaEntropyGraph.Core.LocalEngine;

namespace DnaEntropyGraph.LocalEngine;

/// <summary>
/// Issue #120: asks <c>nvidia-smi</c> for the GPU's name, memory and driver. nvidia-smi ships with the NVIDIA driver, so no
/// NVIDIA driver means no tool means <see cref="GpuDetectionStatus.NoNvidia"/> (an Intel or AMD PC), which is an ordinary
/// answer. Chosen over NVML through P/Invoke because the worker already reads the GPU the same way (<c>worker/gpu.py</c>), so the two
/// agree, and there is no native binding to ship. Never throws.
/// </summary>
public sealed class NvidiaSmiGpuDetector : IGpuDetector
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private readonly IProcessLauncher _launcher;
    private readonly IReadOnlyList<string> _candidates;
    private readonly TimeSpan _timeout;

    /// <param name="candidates">Programs to try in order; full paths (see <see cref="WindowsCandidates"/>).</param>
    public NvidiaSmiGpuDetector(IProcessLauncher launcher, IReadOnlyList<string> candidates, TimeSpan? timeout = null)
    {
        _launcher = launcher;
        _candidates = candidates;
        _timeout = timeout ?? DefaultTimeout;
    }

    /// <summary>
    /// The places the NVIDIA driver puts nvidia-smi on Windows, as full paths: the system folder, the NVSMI folder, then the folders of
    /// <c>PATH</c>. Never a bare name and never a relative <c>PATH</c> entry: a bare name makes Windows look in the app's own folder and the current
    /// folder first, and a program there would run as the user the moment the app looks at the graphics card.
    /// </summary>
    public static IReadOnlyList<string> WindowsCandidates()
        => CandidatesFor(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetEnvironmentVariable("PATH"),
            File.Exists);

    /// <summary>The testable form of <see cref="WindowsCandidates"/>: only paths for which <paramref name="exists"/> is true, in search order.</summary>
    public static IReadOnlyList<string> CandidatesFor(string systemFolder, string programFiles, string? pathVariable, Func<string, bool> exists)
    {
        var all = new List<string>
        {
            Path.Combine(systemFolder, "nvidia-smi.exe"),
            Path.Combine(programFiles, "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe"),
        };
        foreach (var entry in (pathVariable ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var folder = entry.Trim('"');
            if (Path.IsPathFullyQualified(folder))
            {
                all.Add(Path.Combine(folder, "nvidia-smi.exe"));
            }
        }

        return all.Distinct(StringComparer.OrdinalIgnoreCase).Where(exists).ToList();
    }
    public async Task<GpuDetection> DetectAsync(CancellationToken cancellationToken)
    {
        foreach (var candidate in _candidates)
        {
            var output = await _launcher
                .RunToEndAsync(new ProcessSpec(candidate, NvidiaSmiParser.QueryArguments), cancellationToken, _timeout)
                .ConfigureAwait(false);
            if (!output.Started)
            {
                continue;
            }

            if (output.ExitCode != 0)
            {
                return new GpuDetection(GpuDetectionStatus.ToolFailed, []);
            }

            var gpus = NvidiaSmiParser.Parse(output.Text);
            return gpus.Count == 0
                ? new GpuDetection(GpuDetectionStatus.ToolFailed, [])
                : new GpuDetection(GpuDetectionStatus.Found, gpus);
        }

        return new GpuDetection(GpuDetectionStatus.NoNvidia, []);
    }
}

/// <summary>Free space from the drive a path is on.</summary>
public sealed class DriveFreeDiskSpace : IFreeDiskSpace
{
    public long FreeBytes(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrEmpty(root) ? 0 : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException)
        {
            return 0;
        }
    }
}
