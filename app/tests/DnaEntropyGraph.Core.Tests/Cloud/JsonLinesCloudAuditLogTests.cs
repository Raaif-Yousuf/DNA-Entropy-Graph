using System.Text.Json;
using DnaEntropyGraph.Core.Cloud.Audit;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests.Cloud;

/// <summary>Issue #245: the audit file is append-only JSON lines, size-bounded, and never throws.</summary>
public class JsonLinesCloudAuditLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "deg-audit-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static CloudAuditEntry Entry(string op = "Compute.CreateVm") => new(
        new DateTimeOffset(2026, 10, 16, 1, 2, 3, TimeSpan.Zero), "inst", "job-1", op, "zones/z/instances/deg-job-1", "req", "op-1", "ok", null, 12);

    [Fact]
    public void Each_entry_is_one_json_line_and_lines_append()
    {
        var path = Path.Combine(_dir, "logs", "cloud-audit.jsonl");
        var log = new JsonLinesCloudAuditLog(path);

        log.Record(Entry("Compute.CreateVm"));
        log.Record(Entry("Compute.StopVm"));

        var lines = File.ReadAllText(path).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Length.ShouldBe(2);
        File.ReadAllText(path).ShouldNotContain("\r");
        JsonDocument.Parse(lines[0]).RootElement.GetProperty("operation").GetString().ShouldBe("Compute.CreateVm");
        JsonDocument.Parse(lines[1]).RootElement.GetProperty("outcome").GetString().ShouldBe("ok");
        JsonDocument.Parse(lines[1]).RootElement.GetProperty("timeUtc").GetString().ShouldStartWith("2026-10-16T01:02:03");
    }

    [Fact]
    public void The_file_rotates_at_the_limit_and_keeps_one_previous_generation()
    {
        var path = Path.Combine(_dir, "cloud-audit.jsonl");
        var log = new JsonLinesCloudAuditLog(path, maxBytes: 600);

        for (var i = 0; i < 40; i++)
        {
            log.Record(Entry("Op" + i));
        }

        File.Exists(log.RotatedPath).ShouldBeTrue();
        Path.GetFileName(log.RotatedPath).ShouldBe("cloud-audit.1.jsonl");
        new FileInfo(path).Length.ShouldBeLessThan(1200);
        new FileInfo(log.RotatedPath).Length.ShouldBeLessThan(1200);
        File.ReadAllText(path).ShouldContain("Op39");
    }

    [Fact]
    public void A_rotation_that_fails_still_appends_to_the_current_file_and_rotates_once_the_other_file_is_free()
    {
        var path = Path.Combine(_dir, "cloud-audit.jsonl");
        var log = new JsonLinesCloudAuditLog(path, maxBytes: 300);
        for (var i = 0; i < 4; i++)
        {
            log.Record(Entry("Before" + i));
        }

        File.Exists(log.RotatedPath).ShouldBeTrue();
        // A reader holds the previous generation: moving over it fails.
        var held = new FileStream(log.RotatedPath, FileMode.Open, FileAccess.Read, FileShare.None);
        try
        {
            for (var i = 0; i < 4; i++)
            {
                log.Record(Entry("During" + i));
            }

            File.ReadAllText(path).ShouldContain("During3");
        }
        finally
        {
            held.Dispose();
        }

        log.Record(Entry("After"));

        File.ReadAllText(path).ShouldContain("After");
        File.ReadAllText(log.RotatedPath).ShouldContain("During");
    }

    // Holds the cross-process mutex of a log on another thread (a mutex belongs to the thread that took it), as a second process would.
    private static (Action Release, Mutex Held) HoldElsewhere(JsonLinesCloudAuditLog log)
    {
        var taken = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        Mutex? held = null;
        var thread = new Thread(() =>
        {
            held = new Mutex(false, log.MutexName);
            held.WaitOne();
            taken.Set();
            release.Wait();
            held.ReleaseMutex();
        });
        thread.Start();
        taken.Wait(TimeSpan.FromSeconds(5)).ShouldBeTrue();
        return (() =>
        {
            release.Set();
            thread.Join();
            held!.Dispose();
        }, held!);
    }

    [Fact]
    public void Rotation_is_guarded_by_a_named_mutex_and_a_busy_mutex_means_append_only_never_a_rotation_or_a_long_block()
    {
        var path = Path.Combine(_dir, "cloud-audit.jsonl");
        var log = new JsonLinesCloudAuditLog(path, maxBytes: 300);
        for (var i = 0; i < 4; i++)
        {
            log.Record(Entry("Fill" + i));
        }

        File.Exists(log.RotatedPath).ShouldBeTrue();
        File.Delete(log.RotatedPath);
        File.AppendAllText(path, new string('x', 400) + "\n");
        new FileInfo(path).Length.ShouldBeGreaterThanOrEqualTo(300, "the file is over the limit, so the next write would rotate");
        File.Exists(log.RotatedPath).ShouldBeFalse();

        var (release, _) = HoldElsewhere(log);
        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            log.Record(Entry("WhileBusy"));
            clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(3), "a busy rotation lock must never block the caller for long");

            File.ReadAllText(path).ShouldContain("WhileBusy", Case.Sensitive, "the line is appended anyway");
            File.Exists(log.RotatedPath).ShouldBeFalse("another process holds the rotation lock, so this one must not rotate");
        }
        finally
        {
            release();
        }

        log.Record(Entry("AfterRelease"));

        File.Exists(log.RotatedPath).ShouldBeTrue("with the lock free the next write rotates");
        File.ReadAllText(log.RotatedPath).ShouldContain("WhileBusy");
    }

    [Fact]
    public void The_mutex_name_is_stable_per_path_distinct_across_paths_and_global_safe()
    {
        var a = new JsonLinesCloudAuditLog(Path.Combine(_dir, "a.jsonl"));
        var again = new JsonLinesCloudAuditLog(Path.Combine(_dir, "sub", "..", "a.jsonl"));
        var b = new JsonLinesCloudAuditLog(Path.Combine(_dir, "b.jsonl"));

        again.MutexName.ShouldBe(a.MutexName);
        b.MutexName.ShouldNotBe(a.MutexName);
        a.MutexName.ShouldStartWith("Global\\");
        a.MutexName["Global\\".Length..].ShouldNotContain("\\", Case.Sensitive, "a backslash after the Global\\ prefix would be read as a namespace separator");
        a.MutexName.ShouldNotContain(_dir);
        a.MutexName.Length.ShouldBeLessThan(200);
    }

    [Fact]
    public async Task Two_log_instances_on_one_path_write_whole_lines_and_never_throw_while_rotating()
    {
        var path = Path.Combine(_dir, "cloud-audit.jsonl");
        var first = new JsonLinesCloudAuditLog(path, maxBytes: 4000);
        var second = new JsonLinesCloudAuditLog(path, maxBytes: 4000);

        await Task.WhenAll(
            Task.Run(() => Enumerable.Range(0, 60).ToList().ForEach(i => first.Record(Entry("A" + i))), TestContext.Current.CancellationToken),
            Task.Run(() => Enumerable.Range(0, 60).ToList().ForEach(i => second.Record(Entry("B" + i))), TestContext.Current.CancellationToken));

        foreach (var file in new[] { path, first.RotatedPath }.Where(File.Exists))
        {
            foreach (var line in File.ReadAllText(file).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                Should.NotThrow(() => JsonDocument.Parse(line), "every line is a whole JSON object");
            }
        }

        new FileInfo(first.RotatedPath).Length.ShouldBeGreaterThanOrEqualTo(4000, "the previous generation is a full one, not a fresh file moved over it");
    }

    [Fact]
    public void An_unusable_path_loses_the_line_and_never_throws()
    {
        Directory.CreateDirectory(_dir);
        // The path is an existing directory: opening it for append fails.
        var log = new JsonLinesCloudAuditLog(_dir);

        Should.NotThrow(() => log.Record(Entry()));
        Should.NotThrow(() => new JsonLinesCloudAuditLog("\0bad").Record(Entry()));
    }
}
