using System.Diagnostics;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.LocalEngine.Tests;

/// <summary>
/// The real <see cref="SystemProcessLauncher"/> with real tiny child programs (Windows PowerShell and cmd, which every Windows PC has).
/// These cover the parts a fake launcher cannot: argument quoting, the tree kill, the exit handler, the environment and the kill-on-close job.
/// </summary>
public sealed class SystemProcessLauncherTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "deg-launcher-" + Guid.NewGuid().ToString("N"));
    private readonly List<int> _started = [];

    public SystemProcessLauncherTests()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "these tests start Windows programs");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        // Only processes this class started, by id: never by image name.
        foreach (var id in _started)
        {
            try
            {
                using var p = Process.GetProcessById(id);
                p.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
        }

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static async Task<T> Within<T>(Task<T> task, int seconds)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(seconds));
        return await task.WaitAsync(cts.Token);
    }

    private static async Task Within(Task task, int seconds)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(seconds));
        await task.WaitAsync(cts.Token);
    }

    private static string PowerShellExe => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");

    private static string Cmd => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");

    private static async Task<(int Code, List<string> Lines)> RunAsync(ProcessSpec spec, IProcessLauncher? launcher = null)
    {
        var lines = new List<string>();
        using var running = (launcher ?? new SystemProcessLauncher()).Start(spec, line =>
        {
            lock (lines)
            {
                lines.Add(line);
            }
        });
        var code = await Within(running.Exited, 60);
        lock (lines)
        {
            return (code, lines.ToList());
        }
    }

    [Fact]
    public async Task An_argument_with_spaces_and_quotes_in_a_path_reaches_the_program_as_one_argument()
    {
        var script = Path.Combine(_root, "folder with spaces", "echo args.ps1");
        Directory.CreateDirectory(Path.GetDirectoryName(script)!);
        await File.WriteAllTextAsync(script, "$args | ForEach-Object { '[' + $_ + ']' }", TestContext.Current.CancellationToken);
        var tricky = "C:\\My Data\\say \"hello\" there\\";

        var (code, lines) = await RunAsync(new ProcessSpec(PowerShellExe, ["-NoProfile", "-NonInteractive", "-File", script, tricky, "plain", "two words"]));

        code.ShouldBe(0);
        lines.ShouldBe(["[" + tricky + "]", "[plain]", "[two words]"]);
    }

    [Fact]
    public async Task The_exit_code_is_reported_and_every_printed_line_has_arrived_by_then()
    {
        var (code, lines) = await RunAsync(new ProcessSpec(Cmd, ["/c", "echo one&& echo two&& echo three&& exit 7"]));

        code.ShouldBe(7);
        lines.Select(l => l.Trim()).ShouldBe(["one", "two", "three"]);
    }

    [Fact]
    public async Task A_program_that_cannot_start_throws_ProcessStartFailedException()
    {
        var launcher = new SystemProcessLauncher();

        Should.Throw<ProcessStartFailedException>(() => launcher.Start(new ProcessSpec(Path.Combine(_root, "no-such-program.exe"), [])));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Kill_with_entireTree_ends_the_grandchild_too()
    {
        var launcher = new SystemProcessLauncher();
        var pidLine = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var running = launcher.Start(
            new ProcessSpec(PowerShellExe, ["-NoProfile", "-NonInteractive", "-Command", "$p = Start-Process -PassThru -WindowStyle Hidden ping -ArgumentList '-n','120','127.0.0.1'; $p.Id; Start-Sleep 120"]),
            line =>
            {
                if (int.TryParse(line.Trim(), out var id))
                {
                    pidLine.TrySetResult(id);
                }
            });
        var grandchildId = await Within(pidLine.Task, 30);
        _started.Add(grandchildId);
        using var grandchild = Process.GetProcessById(grandchildId);
        grandchild.HasExited.ShouldBeFalse();

        running.Kill(entireTree: true);

        await Within(running.Exited, 30);
        await Within(grandchild.WaitForExitAsync(TestContext.Current.CancellationToken), 30);
        grandchild.HasExited.ShouldBeTrue();
    }

    [Fact]
    public async Task Kill_after_the_program_has_ended_does_nothing()
    {
        using var running = new SystemProcessLauncher().Start(new ProcessSpec(Cmd, ["/c", "exit 0"]));
        await Within(running.Exited, 30);

        Should.NotThrow(() => running.Kill(entireTree: true));
    }

    [Fact]
    public async Task Variables_named_in_ClearEnvironment_are_not_passed_on_but_the_rest_of_the_environment_is()
    {
        const string Name = "DEG_TEST_SCRUB_ME";
        const string Keep = "DEG_TEST_KEEP_ME";
        Environment.SetEnvironmentVariable(Name, "leaked");
        Environment.SetEnvironmentVariable(Keep, "kept");
        try
        {
            var (_, lines) = await RunAsync(new ProcessSpec(Cmd, ["/c", $"echo [%{Name}%][%{Keep}%]"], ClearEnvironment: [Name]));

            lines.Single().Trim().ShouldBe($"[%{Name}%][kept]");
        }
        finally
        {
            Environment.SetEnvironmentVariable(Name, null);
            Environment.SetEnvironmentVariable(Keep, null);
        }
    }

    [Fact]
    public async Task A_variable_in_both_ClearEnvironment_and_Environment_gets_the_value_in_Environment()
    {
        const string Name = "DEG_TEST_SCRUB_AND_SET";
        Environment.SetEnvironmentVariable(Name, "inherited");
        try
        {
            var spec = new ProcessSpec(Cmd, ["/c", $"echo [%{Name}%]"], Environment: new Dictionary<string, string> { [Name] = "explicit" }, ClearEnvironment: [Name]);

            var (_, lines) = await RunAsync(spec);

            lines.Single().Trim().ShouldBe("[explicit]");
        }
        finally
        {
            Environment.SetEnvironmentVariable(Name, null);
        }
    }

    private sealed class RecordingGroup : IChildProcessGroup
    {
        public List<int> Assigned { get; } = [];

        public bool Throw { get; init; }

        public void Assign(Process process)
        {
            Assigned.Add(process.Id);
            if (Throw)
            {
                throw new InvalidOperationException("no job for you");
            }
        }
    }

    [Fact]
    public async Task Every_started_program_is_handed_to_the_child_process_group()
    {
        var group = new RecordingGroup();
        var launcher = new SystemProcessLauncher(group);
        using var running = launcher.Start(new ProcessSpec(Cmd, ["/c", "exit 0"]));
        await Within(running.Exited, 30);

        group.Assigned.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_group_that_cannot_take_the_process_does_not_stop_the_run()
    {
        var launcher = new SystemProcessLauncher(new RecordingGroup { Throw = true });

        var (code, _) = await RunAsync(new ProcessSpec(Cmd, ["/c", "exit 3"]), launcher);

        code.ShouldBe(3);
    }

    [Fact]
    public async Task Closing_the_kill_on_close_job_ends_the_child_the_way_a_killed_app_would()
    {
        // MEASURED 2026-10-15 (review of #174): a killed app left the worker running because nothing tied the child to the app.
        var job = new WindowsKillOnCloseJob();
        var launcher = new SystemProcessLauncher(job);
        using var running = launcher.Start(new ProcessSpec(Cmd, ["/c", "ping -n 120 127.0.0.1 > nul"]));
        await Task.Delay(500, TestContext.Current.CancellationToken);
        running.Exited.IsCompleted.ShouldBeFalse();

        job.Dispose();

        await Within(running.Exited, 30); // ends within seconds, not after the 120 s ping
    }
}
