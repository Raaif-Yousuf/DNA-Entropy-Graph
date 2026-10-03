using System.Text;
using DnaEntropyGraph.Core.Diagnostics;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests.Diagnostics;

public class FolderDiagnosticsSourceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "deg-src-" + Guid.NewGuid().ToString("n"));

    public FolderDiagnosticsSourceTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Seed(string relative, string text)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public void Lists_settings_logs_and_runs_only_never_tokens_inputs_or_the_database()
    {
        Seed("settings.json", "{}");
        Seed("logs/app.log", "x");
        Seed("runs/job-1/status.json", "{}");
        Seed("auth/sub.token", "secret");
        Seed("inputs/job-1/a.fasta", ">x");
        Seed("pasted/p.fasta", ">x");
        Seed("app.db", "db");

        var files = new FolderDiagnosticsSource(_root).ListFiles();

        files.Order().ShouldBe(["logs/app.log", "runs/job-1/status.json", "settings.json"]);
    }

    [Fact]
    public void A_missing_folder_lists_nothing()
    {
        new FolderDiagnosticsSource(Path.Combine(_root, "nope")).ListFiles().ShouldBeEmpty();
    }

    [Fact]
    public void Reads_a_file_another_process_still_has_open_and_refuses_paths_outside_the_folder()
    {
        Seed("logs/app.log", "line one");
        var source = new FolderDiagnosticsSource(_root);

        using var writer = new FileStream(Path.Combine(_root, "logs/app.log"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

        var read = source.TryRead("logs/app.log", 1000)!;
        Encoding.UTF8.GetString(read.Bytes).ShouldBe("line one");
        read.Truncated.ShouldBeFalse();
        source.TryRead("../outside.txt", 1000).ShouldBeNull();
        source.TryRead("logs/missing.log", 1000).ShouldBeNull();
    }

    [Fact]
    public void A_file_longer_than_the_cap_returns_only_its_last_bytes_marked_truncated()
    {
        Seed("logs/big.log", "0123456789ABCDEFGHIJ");

        var read = new FolderDiagnosticsSource(_root).TryRead("logs/big.log", 5)!;

        Encoding.UTF8.GetString(read.Bytes).ShouldBe("FGHIJ");
        read.Truncated.ShouldBeTrue();
    }
}
