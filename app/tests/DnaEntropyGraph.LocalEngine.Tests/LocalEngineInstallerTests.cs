using DnaEntropyGraph.Core.LocalEngine;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.LocalEngine.Tests;

public sealed class LocalEngineInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "deg-installer-" + Guid.NewGuid().ToString("N"));
    private readonly string _uv;
    private readonly string _lock;
    private readonly string _wheel;
    private readonly string _engine;

    public LocalEngineInstallerTests()
    {
        Directory.CreateDirectory(_root);
        _uv = Path.Combine(_root, "uv.exe");
        _lock = Path.Combine(_root, "requirements-win-cu12x.lock");
        _wheel = Path.Combine(_root, "dna_entropy-0.0.1-py3-none-any.whl");
        File.WriteAllText(_uv, "x");
        File.WriteAllText(_lock, "torch==2.7.1 --hash=sha256:00\n");
        File.WriteAllText(_wheel, "x");
        _engine = Path.Combine(_root, "data", "engine");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static readonly LocalEngineHealth Healthy = new(LocalEngineState.Healthy, "0.0.1", true, true, string.Empty);

    private LocalEngineInstaller Installer(
        FakeProcessLauncher launcher,
        LocalEngineHealth? health = null,
        string? uv = null,
        string? lockFile = null)
        => new(
            launcher,
            new LocalEngineLayout(_engine),
            uv ?? _uv,
            lockFile ?? _lock,
            _wheel,
            Path.Combine(_root, "data"),
            _ => Task.FromResult(health ?? Healthy));

    private static FakeProcessLauncher AlwaysOk()
        => new(_ =>
        {
            var p = new FakeProcess();
            p.Print("done");
            p.End(0);
            return p;
        });

    private sealed class Steps : IProgress<LocalInstallProgress>
    {
        public List<LocalInstallStep> Seen { get; } = [];

        public void Report(LocalInstallProgress value)
        {
            if (Seen.Count == 0 || Seen[^1] != value.Step)
            {
                Seen.Add(value.Step);
            }
        }
    }

    [Fact]
    public async Task Install_makes_a_managed_python_312_venv_syncs_the_lock_installs_the_worker_and_checks_health()
    {
        var launcher = AlwaysOk();
        var steps = new Steps();

        var result = await Installer(launcher).InstallAsync(steps, CancellationToken.None);

        result.Succeeded.ShouldBeTrue();
        var venv = Path.Combine(_engine, ".venv");
        var python = Path.Combine(venv, "Scripts", "python.exe");
        launcher.Started.Count.ShouldBe(3);
        launcher.Started[0].FileName.ShouldBe(_uv);
        launcher.Started[0].Arguments.ShouldBe(["venv", "--python", "3.12", "--managed-python", venv]);
        launcher.Started[1].Arguments.ShouldBe(["pip", "sync", "--python", python, "--require-hashes", _lock]);
        launcher.Started[2].Arguments.ShouldBe(["pip", "install", "--python", python, "--no-deps", _wheel]);
        steps.Seen.ShouldBe([LocalInstallStep.CreatingEnvironment, LocalInstallStep.InstallingPackages, LocalInstallStep.InstallingWorker, LocalInstallStep.Checking]);
    }

    [Fact]
    public async Task Everything_uv_writes_stays_inside_the_engine_folder_so_Remove_is_one_folder()
    {
        var launcher = AlwaysOk();

        await Installer(launcher).InstallAsync(null, CancellationToken.None);

        foreach (var spec in launcher.Started)
        {
            spec.Environment!["UV_PYTHON_INSTALL_DIR"].ShouldStartWith(_engine);
            spec.Environment["UV_CACHE_DIR"].ShouldStartWith(_engine);
        }
    }

    [Fact]
    public async Task A_missing_uv_is_reported_and_nothing_is_started()
    {
        var launcher = AlwaysOk();

        var result = await Installer(launcher, uv: Path.Combine(_root, "nope.exe")).InstallAsync(null, CancellationToken.None);

        result.ErrorCode.ShouldBe(LocalEngineErrorCodes.UvMissing);
        launcher.Started.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_missing_or_empty_lock_is_reported_and_nothing_is_started(bool empty)
    {
        var launcher = AlwaysOk();
        var lockFile = Path.Combine(_root, "empty.lock");
        if (empty)
        {
            File.WriteAllText(lockFile, "  \n");
        }

        var result = await Installer(launcher, lockFile: lockFile).InstallAsync(null, CancellationToken.None);

        result.ErrorCode.ShouldBe(LocalEngineErrorCodes.LockMissing);
        launcher.Started.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_failed_sync_stops_there_and_names_the_step_in_the_detail()
    {
        var launcher = new FakeProcessLauncher(spec =>
        {
            var p = new FakeProcess();
            p.End(spec.Arguments.Contains("sync") ? 1 : 0);
            return p;
        });

        var result = await Installer(launcher).InstallAsync(null, CancellationToken.None);

        result.ErrorCode.ShouldBe(LocalEngineErrorCodes.InstallFailed);
        result.Detail!.ShouldContain("InstallingPackages");
        launcher.Started.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_uv_that_cannot_start_is_a_failed_install()
    {
        var result = await Installer(new FakeProcessLauncher(_ => null)).InstallAsync(null, CancellationToken.None);

        result.ErrorCode.ShouldBe(LocalEngineErrorCodes.InstallFailed);
    }

    [Fact]
    public async Task An_install_whose_worker_does_not_pass_the_health_check_is_a_failed_check()
    {
        var result = await Installer(AlwaysOk(), health: LocalEngineHealth.NotInstalled()).InstallAsync(null, CancellationToken.None);

        result.Succeeded.ShouldBeFalse();
        result.ErrorCode.ShouldBe(LocalEngineErrorCodes.CheckFailed);
    }

    [Fact]
    public async Task A_cancel_kills_the_running_step_and_reports_cancelled()
    {
        var running = new FakeProcess();
        using var cts = new CancellationTokenSource();
        var launcher = new FakeProcessLauncher(_ => running);
        var install = Installer(launcher).InstallAsync(null, cts.Token);

        await cts.CancelAsync();
        var result = await install;

        result.ErrorCode.ShouldBe(LocalEngineErrorCodes.Cancelled);
        running.KilledTree.ShouldBeTrue();
    }

    [Fact]
    public async Task Repair_of_an_existing_venv_only_syncs_and_reinstalls_the_worker()
    {
        var python = Path.Combine(_engine, ".venv", "Scripts", "python.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(python)!);
        File.WriteAllText(python, "x");
        var launcher = AlwaysOk();

        var result = await Installer(launcher).RepairAsync(null, CancellationToken.None);

        result.Succeeded.ShouldBeTrue();
        launcher.Started.Select(s => s.Arguments[0] + " " + s.Arguments[1]).ShouldBe(["pip sync", "pip install"]);
    }

    [Fact]
    public async Task Repair_with_no_venv_is_a_full_install()
    {
        var launcher = AlwaysOk();

        await Installer(launcher).RepairAsync(null, CancellationToken.None);

        launcher.Started[0].Arguments[0].ShouldBe("venv");
    }

    [Fact]
    public async Task Remove_deletes_the_engine_folder_and_only_it()
    {
        Directory.CreateDirectory(Path.Combine(_engine, ".venv"));
        File.WriteAllText(Path.Combine(_engine, ".venv", "x.txt"), "x");
        var sibling = Path.Combine(_root, "data", "runs");
        Directory.CreateDirectory(sibling);

        var result = await Installer(AlwaysOk()).RemoveAsync(CancellationToken.None);

        result.Succeeded.ShouldBeTrue();
        Directory.Exists(_engine).ShouldBeFalse();
        Directory.Exists(sibling).ShouldBeTrue();
    }

    [Fact]
    public async Task Remove_with_nothing_installed_succeeds()
    {
        (await Installer(AlwaysOk()).RemoveAsync(CancellationToken.None)).Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    public async Task Remove_refuses_a_folder_that_is_not_strictly_inside_the_app_data_folder(string relative)
    {
        var dataRoot = Path.Combine(_root, "data");
        Directory.CreateDirectory(dataRoot);
        File.WriteAllText(Path.Combine(dataRoot, "keep.txt"), "x");
        var installer = new LocalEngineInstaller(
            AlwaysOk(),
            new LocalEngineLayout(Path.GetFullPath(Path.Combine(dataRoot, relative))),
            _uv,
            _lock,
            _wheel,
            dataRoot,
            _ => Task.FromResult(Healthy));

        var result = await installer.RemoveAsync(CancellationToken.None);

        result.ErrorCode.ShouldBe(LocalEngineErrorCodes.RemoveRefused);
        File.Exists(Path.Combine(dataRoot, "keep.txt")).ShouldBeTrue();
    }
}
