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

    /// <param name="candidates">Programs to try in order: the PATH name first, then the install folders the driver uses.</param>
    public NvidiaSmiGpuDetector(IProcessLauncher launcher, IReadOnlyList<string> candidates, TimeSpan? timeout = null)
    {
        _launcher = launcher;
        _candidates = candidates;
        _timeout = timeout ?? DefaultTimeout;
    }

    /// <summary>The places the NVIDIA driver puts nvidia-smi on Windows.</summary>
    public static IReadOnlyList<string> WindowsCandidates()
    {
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        return
        [
            "nvidia-smi",
            Path.Combine(system, "nvidia-smi.exe"),
            Path.Combine(programFiles, "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe"),
        ];
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
