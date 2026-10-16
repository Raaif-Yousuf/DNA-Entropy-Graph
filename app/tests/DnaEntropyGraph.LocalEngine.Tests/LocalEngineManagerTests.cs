using DnaEntropyGraph.Core.LocalEngine;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.LocalEngine.Tests;

public sealed class LocalEngineManagerTests
{
    private const long Gib = 1024L * 1024 * 1024;
    private static readonly LocalEngineLayout Layout = new(@"C:\data\engine");

    private static LocalEngineManager Manager(
        FakeProcessLauncher launcher,
        GpuDetection? gpu = null,
        long freeBytes = 100 * Gib,
        bool pythonExists = true,
        string appVersion = "0.0.1")
    {
        var detector = Substitute.For<IGpuDetector>();
        detector.DetectAsync(Arg.Any<CancellationToken>()).Returns(gpu ?? new GpuDetection(GpuDetectionStatus.NoNvidia, []));
        var disk = Substitute.For<IFreeDiskSpace>();
        disk.FreeBytes(Arg.Any<string>()).Returns(freeBytes);
        return new LocalEngineManager(detector, disk, launcher, Layout, appVersion, path => pythonExists && path == Layout.PythonExe);
    }

    [Fact]
    public async Task No_python_in_the_engine_folder_is_NotInstalled_and_starts_nothing()
    {
        var launcher = new FakeProcessLauncher(_ => null);

        var health = await Manager(launcher, pythonExists: false).CheckHealthAsync(CancellationToken.None);

        health.State.ShouldBe(LocalEngineState.NotInstalled);
        launcher.Started.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_worker_of_the_apps_version_with_cuda_is_Healthy_and_was_asked_through_the_engines_python()
    {
        var launcher = FakeProcessLauncher.Answering(("python", 0, "{\"worker\": \"0.0.1\", \"evo\": true, \"cuda\": true}\n"));

        var health = await Manager(launcher).CheckHealthAsync(CancellationToken.None);

        health.State.ShouldBe(LocalEngineState.Healthy);
        health.EvoInstalled.ShouldBeTrue();
        health.CudaAvailable.ShouldBeTrue();
        launcher.Started.Single().FileName.ShouldBe(Layout.PythonExe);
        launcher.Started.Single().Arguments.ShouldBe(["-m", "dna_entropy.worker.cli", "health"]);
    }

    [Fact]
    public async Task A_worker_without_the_evo_extra_is_Healthy_for_the_mock_but_has_no_cuda()
    {
        var launcher = FakeProcessLauncher.Answering(("python", 0, "{\"worker\": \"0.0.1\", \"evo\": false, \"cuda\": false}"));

        var health = await Manager(launcher).CheckHealthAsync(CancellationToken.None);

        health.State.ShouldBe(LocalEngineState.Healthy);
        health.CudaAvailable.ShouldBeFalse();
    }

    [Fact]
    public async Task Noise_before_the_json_line_is_ignored()
    {
        var launcher = FakeProcessLauncher.Answering(("python", 0, "some warning\n{\"worker\": \"0.0.1\", \"evo\": true, \"cuda\": true}\n"));

        (await Manager(launcher).CheckHealthAsync(CancellationToken.None)).State.ShouldBe(LocalEngineState.Healthy);
    }

    [Fact]
    public async Task A_different_worker_version_is_a_mismatch()
    {
        var launcher = FakeProcessLauncher.Answering(("python", 0, "{\"worker\": \"0.0.0\", \"evo\": true, \"cuda\": true}"));

        var health = await Manager(launcher).CheckHealthAsync(CancellationToken.None);

        health.State.ShouldBe(LocalEngineState.WorkerVersionMismatch);
        health.WorkerVersion.ShouldBe("0.0.0");
    }

    [Theory]
    [InlineData(1, "Traceback (most recent call last):")]
    [InlineData(0, "not json at all")]
    [InlineData(0, "{\"evo\": true}")]
    [InlineData(0, "")]
    public async Task An_engine_that_does_not_answer_properly_is_Broken(int exitCode, string output)
    {
        var launcher = FakeProcessLauncher.Answering(("python", exitCode, output));

        (await Manager(launcher).CheckHealthAsync(CancellationToken.None)).State.ShouldBe(LocalEngineState.Broken);
    }

    [Fact]
    public async Task A_python_that_cannot_start_is_Broken()
    {
        var launcher = new FakeProcessLauncher(_ => null);

        (await Manager(launcher).CheckHealthAsync(CancellationToken.None)).State.ShouldBe(LocalEngineState.Broken);
    }

    [Fact]
    public async Task Readiness_joins_the_hardware_and_the_engine()
    {
        var launcher = FakeProcessLauncher.Answering(("python", 0, "{\"worker\": \"0.0.1\", \"evo\": true, \"cuda\": true}"));
        var gpu = new GpuDetection(GpuDetectionStatus.Found, [new GpuInfo("NVIDIA GeForce RTX 4090", 24564, "551.61")]);

        var readiness = await Manager(launcher, gpu).CheckAsync(CancellationToken.None);

        readiness.Capability.Eligible.ShouldBeTrue();
        readiness.CanRun(needsGpu: true).ShouldBeTrue();
        readiness.CanRun(needsGpu: false).ShouldBeTrue();
    }

    [Fact]
    public async Task A_healthy_engine_on_a_PC_with_no_nvidia_runs_the_mock_but_not_a_real_model()
    {
        var launcher = FakeProcessLauncher.Answering(("python", 0, "{\"worker\": \"0.0.1\", \"evo\": false, \"cuda\": false}"));

        var readiness = await Manager(launcher).CheckAsync(CancellationToken.None);

        readiness.Capability.Blockers.ShouldBe([LocalBlocker.NoNvidiaGpu]);
        readiness.CanRun(needsGpu: false).ShouldBeTrue();
        readiness.CanRun(needsGpu: true).ShouldBeFalse();
    }

    [Fact]
    public async Task Eligible_hardware_with_a_torch_that_sees_no_cuda_cannot_run_a_real_model()
    {
        var launcher = FakeProcessLauncher.Answering(("python", 0, "{\"worker\": \"0.0.1\", \"evo\": true, \"cuda\": false}"));
        var gpu = new GpuDetection(GpuDetectionStatus.Found, [new GpuInfo("NVIDIA GeForce RTX 4090", 24564, "551.61")]);

        (await Manager(launcher, gpu).CheckAsync(CancellationToken.None)).CanRun(needsGpu: true).ShouldBeFalse();
    }

    [Fact]
    public async Task The_disk_is_measured_where_the_engine_lives()
    {
        var detector = Substitute.For<IGpuDetector>();
        detector.DetectAsync(Arg.Any<CancellationToken>()).Returns(new GpuDetection(GpuDetectionStatus.NoNvidia, []));
        var disk = Substitute.For<IFreeDiskSpace>();
        disk.FreeBytes(Arg.Any<string>()).Returns(5 * Gib);
        var manager = new LocalEngineManager(detector, disk, new FakeProcessLauncher(_ => null), Layout, "0.0.1", _ => false);

        var readiness = await manager.CheckAsync(CancellationToken.None);

        disk.Received().FreeBytes(Layout.EngineFolder);
        readiness.Capability.FreeDiskBytes.ShouldBe(5 * Gib);
    }
}
