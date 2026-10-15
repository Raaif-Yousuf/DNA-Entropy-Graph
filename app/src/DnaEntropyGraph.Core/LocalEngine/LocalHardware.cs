using System.Globalization;

namespace DnaEntropyGraph.Core.LocalEngine;

/// <summary>One NVIDIA GPU as <c>nvidia-smi</c> reports it. <see cref="MemoryMiB"/> is the card's total memory.</summary>
public sealed record GpuInfo(string Name, long MemoryMiB, string DriverVersion);

public enum GpuDetectionStatus
{
    /// <summary>At least one NVIDIA GPU was read.</summary>
    Found,

    /// <summary>No NVIDIA tool exists on this PC (the normal state of an Intel or AMD machine).</summary>
    NoNvidia,

    /// <summary>The NVIDIA tool exists but gave no usable answer (a driver that is not loaded, a timeout, odd output).</summary>
    ToolFailed,
}

public sealed record GpuDetection(GpuDetectionStatus Status, IReadOnlyList<GpuInfo> Gpus);

/// <summary>
/// Reads this PC's NVIDIA GPU (issue #120). Never throws: "no NVIDIA" is an ordinary answer. The real implementation
/// (<c>NvidiaSmiGpuDetector</c> in DnaEntropyGraph.LocalEngine) runs <c>nvidia-smi</c>; tests use a fake.
/// </summary>
public interface IGpuDetector
{
    Task<GpuDetection> DetectAsync(CancellationToken cancellationToken);
}

/// <summary>Free disk space where the local engine and its runs live; behind an interface so tests need no real disk.</summary>
public interface IFreeDiskSpace
{
    /// <summary>Bytes free on the drive that holds <paramref name="path"/>, or 0 when it cannot be read.</summary>
    long FreeBytes(string path);
}

/// <summary>Parses <c>nvidia-smi --query-gpu=name,memory.total,driver_version --format=csv,noheader,nounits</c>.</summary>
public static class NvidiaSmiParser
{
    /// <summary>The arguments the parser's input is produced with.</summary>
    public static readonly string[] QueryArguments =
    [
        "--query-gpu=name,memory.total,driver_version",
        "--format=csv,noheader,nounits",
    ];

    /// <summary>The GPUs in the text; a line that is not <c>name, memory, driver</c> is skipped, never an exception.</summary>
    public static IReadOnlyList<GpuInfo> Parse(string? text)
    {
        var gpus = new List<GpuInfo>();
        foreach (var line in (text ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length != 3 || parts[0].Length == 0 || parts[2].Length == 0)
            {
                continue;
            }

            var digits = new string(parts[1].TakeWhile(char.IsAsciiDigit).ToArray());
            if (digits.Length == 0 || !long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var mib))
            {
                continue;
            }

            gpus.Add(new GpuInfo(parts[0], mib, parts[2]));
        }

        return gpus;
    }
}

public enum LocalBlocker
{
    NoNvidiaGpu,
    NotEnoughGpuMemory,
    DriverTooOld,
    NotEnoughDisk,
}

/// <summary>What this PC can do. <see cref="Eligible"/> is false when any <see cref="Blockers"/> remain.</summary>
public sealed record LocalCapability(bool Eligible, IReadOnlyList<LocalBlocker> Blockers, GpuInfo? Gpu, long FreeDiskBytes);

/// <summary>
/// The thresholds of issue #120: a GPU with about 24 GB, a driver of 550 or newer, 30 GB free on disk.
/// DECISION (agent-made, reversible): the memory floor is 22000 MiB, not 24576. A card sold as 24 GB reports
/// 23034 MiB (L4, A10G) to 24564 MiB (RTX 4090), and refusing those would refuse every 24 GB card. A 16 GB card
/// (16376 MiB) is still refused; the 7B model needs about 14 GB of weights plus activations.
/// </summary>
public static class LocalCapabilityEvaluator
{
    public const long MinGpuMemoryMiB = 22_000;
    public const int MinDriverMajor = 550;
    public const long MinFreeDiskBytes = 30L * 1024 * 1024 * 1024;

    public static LocalCapability Evaluate(GpuDetection detection, long freeDiskBytes)
    {
        ArgumentNullException.ThrowIfNull(detection);
        var gpu = detection.Status == GpuDetectionStatus.Found ? detection.Gpus.OrderByDescending(g => g.MemoryMiB).FirstOrDefault() : null;
        if (gpu is null)
        {
            return new LocalCapability(false, [LocalBlocker.NoNvidiaGpu], null, freeDiskBytes);
        }

        var blockers = new List<LocalBlocker>();
        if (gpu.MemoryMiB < MinGpuMemoryMiB)
        {
            blockers.Add(LocalBlocker.NotEnoughGpuMemory);
        }

        if (!DriverIsNewEnough(gpu.DriverVersion))
        {
            blockers.Add(LocalBlocker.DriverTooOld);
        }

        if (freeDiskBytes < MinFreeDiskBytes)
        {
            blockers.Add(LocalBlocker.NotEnoughDisk);
        }

        return new LocalCapability(blockers.Count == 0, blockers, gpu, freeDiskBytes);
    }

    private static bool DriverIsNewEnough(string version)
    {
        var major = new string(version.TakeWhile(char.IsAsciiDigit).ToArray());
        return int.TryParse(major, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= MinDriverMajor;
    }
}
