using DnaEntropyGraph.Core.Inputs;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests;

/// <summary>Issue #460 / Hard Rule 14: a copy of every input is kept under app data; the user's file is never touched and nothing is written next to it.</summary>
public sealed class LocalRunInputStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "deg-input-store-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string UserFile(string name, string content = "LOCUS x")
    {
        var dir = Path.Combine(_root, "user");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task Copies_the_input_under_the_app_data_run_folder_with_identical_bytes()
    {
        var source = UserFile("SetTnpB.gb", "LOCUS SetTnpB\nORIGIN\n        1 acgt\n//\n");
        var store = new LocalRunInputStore(Path.Combine(_root, "appdata"));

        var staged = await store.StageAsync("job-1", source, CancellationToken.None);

        staged.FileName.ShouldBe("SetTnpB.gb");
        staged.LocalPath.ShouldBe(Path.Combine(_root, "appdata", "runs", "job-1", "input", "SetTnpB.gb"));
        File.ReadAllBytes(staged.LocalPath).ShouldBe(File.ReadAllBytes(source));
    }

    [Fact]
    public async Task Never_modifies_the_source_and_writes_nothing_next_to_it()
    {
        var source = UserFile("a.fasta", ">x\nACGT\n");
        var before = (File.ReadAllText(source), File.GetLastWriteTimeUtc(source));
        var store = new LocalRunInputStore(Path.Combine(_root, "appdata"));

        await store.StageAsync("job-1", source, CancellationToken.None);

        (File.ReadAllText(source), File.GetLastWriteTimeUtc(source)).ShouldBe(before);
        Directory.GetFileSystemEntries(Path.GetDirectoryName(source)!).ShouldBe([source]);
    }

    [Fact]
    public async Task A_missing_source_throws_file_not_found_and_creates_no_run_folder()
    {
        var store = new LocalRunInputStore(Path.Combine(_root, "appdata"));

        await Should.ThrowAsync<FileNotFoundException>(() => store.StageAsync("job-1", Path.Combine(_root, "nope.gb"), CancellationToken.None));

        Directory.Exists(Path.Combine(_root, "appdata", "runs", "job-1")).ShouldBeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("../x")]
    [InlineData("a/b")]
    public async Task A_job_id_that_could_leave_the_runs_folder_is_refused(string jobId)
    {
        var store = new LocalRunInputStore(Path.Combine(_root, "appdata"));

        await Should.ThrowAsync<ArgumentException>(() => store.StageAsync(jobId, UserFile("a.gb"), CancellationToken.None));
    }

    [Fact]
    public async Task Staging_the_same_job_twice_replaces_the_copy_instead_of_failing()
    {
        var source = UserFile("a.gb", "one");
        var store = new LocalRunInputStore(Path.Combine(_root, "appdata"));
        await store.StageAsync("job-1", source, CancellationToken.None);
        File.WriteAllText(source, "two");

        var staged = await store.StageAsync("job-1", source, CancellationToken.None);

        File.ReadAllText(staged.LocalPath).ShouldBe("two");
    }

    [Fact]
    public async Task A_cancelled_token_stops_before_copying()
    {
        var store = new LocalRunInputStore(Path.Combine(_root, "appdata"));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => store.StageAsync("job-1", UserFile("a.gb"), cts.Token));
    }
}
