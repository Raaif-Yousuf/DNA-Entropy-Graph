using DnaEntropyGraph.Core.LocalEngine;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.LocalEngine.Tests;

public sealed class NvidiaSmiGpuDetectorTests
{
    [Fact]
    public async Task A_working_nvidia_smi_gives_the_gpus_and_asks_for_csv_without_units()
    {
        var launcher = FakeProcessLauncher.Answering(("nvidia-smi", 0, "NVIDIA GeForce RTX 4090, 24564, 551.61\n"));
        var detector = new NvidiaSmiGpuDetector(launcher, ["nvidia-smi"]);

        var result = await detector.DetectAsync(CancellationToken.None);

        result.Status.ShouldBe(GpuDetectionStatus.Found);
        result.Gpus.Single().MemoryMiB.ShouldBe(24564);
        launcher.Started.Single().Arguments.ShouldBe(NvidiaSmiParser.QueryArguments);
    }

    [Fact]
    public async Task No_nvidia_smi_anywhere_is_NoNvidia_which_is_this_laptop()
    {
        var launcher = new FakeProcessLauncher(_ => null);
        var detector = new NvidiaSmiGpuDetector(launcher, ["nvidia-smi", @"C:\Windows\System32\nvidia-smi.exe"]);

        var result = await detector.DetectAsync(CancellationToken.None);

        result.Status.ShouldBe(GpuDetectionStatus.NoNvidia);
        result.Gpus.ShouldBeEmpty();
        launcher.Started.Count.ShouldBe(2);
    }

    [Fact]
    public async Task The_second_candidate_is_tried_when_the_first_does_not_start()
    {
        var launcher = new FakeProcessLauncher(spec =>
        {
            if (!spec.FileName.StartsWith("C:", StringComparison.Ordinal))
            {
                return null;
            }

            var process = new FakeProcess();
            process.Print("NVIDIA L4, 23034, 550.90");
            process.End(0);
            return process;
        });
        var detector = new NvidiaSmiGpuDetector(launcher, ["nvidia-smi", @"C:\Windows\System32\nvidia-smi.exe"]);

        (await detector.DetectAsync(CancellationToken.None)).Status.ShouldBe(GpuDetectionStatus.Found);
    }

    [Fact]
    public async Task A_driver_that_is_not_loaded_is_ToolFailed()
    {
        var launcher = FakeProcessLauncher.Answering(("nvidia-smi", 9, "NVIDIA-SMI has failed because it couldn't communicate with the NVIDIA driver.\n"));

        var result = await new NvidiaSmiGpuDetector(launcher, ["nvidia-smi"]).DetectAsync(CancellationToken.None);

        result.Status.ShouldBe(GpuDetectionStatus.ToolFailed);
    }

    [Fact]
    public async Task Exit_zero_with_unparseable_text_is_ToolFailed()
    {
        var launcher = FakeProcessLauncher.Answering(("nvidia-smi", 0, "hello\n"));

        (await new NvidiaSmiGpuDetector(launcher, ["nvidia-smi"]).DetectAsync(CancellationToken.None)).Status.ShouldBe(GpuDetectionStatus.ToolFailed);
    }

    [Fact]
    public async Task A_tool_that_never_answers_is_killed_and_is_ToolFailed()
    {
        var hung = new FakeProcess();
        var launcher = new FakeProcessLauncher(_ => hung);
        var detector = new NvidiaSmiGpuDetector(launcher, ["nvidia-smi"], timeout: TimeSpan.FromMilliseconds(50));

        var result = await detector.DetectAsync(CancellationToken.None);

        result.Status.ShouldBe(GpuDetectionStatus.ToolFailed);
        hung.KilledTree.ShouldBeTrue();
    }

    [Fact]
    public void Candidates_are_full_paths_system_folder_first_then_program_files_then_path_folders()
    {
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\Windows\System32\nvidia-smi.exe",
            @"C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe",
            @"D:\tools\nvidia-smi.exe",
        };

        var list = NvidiaSmiGpuDetector.CandidatesFor(@"C:\Windows\System32", @"C:\Program Files", @"C:\Windows;D:\tools;E:\nothing", present.Contains);

        list.ShouldBe(
        [
            @"C:\Windows\System32\nvidia-smi.exe",
            @"C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe",
            @"D:\tools\nvidia-smi.exe",
        ]);
    }

    [Fact]
    public void A_relative_or_empty_path_entry_is_never_searched_so_the_current_folder_cannot_supply_nvidia_smi()
    {
        var list = NvidiaSmiGpuDetector.CandidatesFor(@"C:\Windows\System32", @"C:\Program Files", @".;;bin;..\x;D:\tools", _ => true);

        list.ShouldAllBe(p => Path.IsPathFullyQualified(p));
        list.ShouldNotContain("nvidia-smi");
        list.Any(p => p.StartsWith(@"D:\tools", StringComparison.OrdinalIgnoreCase)).ShouldBeTrue();
        list.Count.ShouldBe(3);
    }}
