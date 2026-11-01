using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Runs;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>
/// Issue #1043 (Hard Rule 14): a resumed run reuses the folder its history row names only when that folder carries the run's own marker
/// (<see cref="RunFolderMarker"/>). Driven through <see cref="JobReconciler"/>, the way a launch resumes a killed run, with a row whose options name
/// no output folder (so the reconciler falls back to the row's own folder).
/// </summary>
public sealed class JobReconcilerResumeMarkerTests
{
    private static readonly string[] Snapshot = [];

    private static async Task<(JobReconcilerTests.Env Env, string Victim)> SeedRowNamingAsync(string jobId, Action<string>? prepare)
    {
        var env = new JobReconcilerTests.Env();
        var victim = Path.Combine(Path.GetTempPath(), "deg-1043-" + Guid.NewGuid().ToString("N"), "someones-folder");
        Directory.CreateDirectory(victim);
        prepare?.Invoke(victim);
        await env.SeedAsync(jobId, JobPhase.Running, vm: true, tweak: row =>
        {
            var options = RunOptionsJson.TryDeserialize(row.OptionsJson)!;
            return row with { OptionsJson = RunOptionsJson.Serialize(options with { OutputFolder = null }), OutputDir = victim };
        });
        return (env, victim);
    }

    private static string[] Entries(string folder) => Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories).Select(Path.GetFileName).OfType<string>().Order().ToArray();

    [Fact]
    public async Task A_row_whose_folder_has_no_marker_is_not_written_into_and_the_run_gets_a_fresh_marked_folder()
    {
        var (env, victim) = await SeedRowNamingAsync("job-1043-a", prepare: null);

        var outcome = (await env.Reconciler().ReattachAsync(CancellationToken.None)).Single(o => o.JobId == "job-1043-a");

        outcome.FinalPhase.ShouldBe(JobPhase.Completed, outcome.ErrorCode);
        Entries(victim).ShouldBe(Snapshot, "nothing may be written into a folder the app cannot prove it made for this run");
        var folder = env.Row("job-1043-a").OutputDir.ShouldNotBeNull();
        folder.ShouldNotBe(victim);
        Path.GetDirectoryName(folder).ShouldBe(Path.Combine(env.AppData, "downloads"), "the fresh folder is under the default output folder, never beside the row's folder");
        RunFolderMarker.Check(folder, "job-1043-a").ShouldBe(RunFolderMarkerState.Matches);
        Directory.EnumerateFiles(folder, "*.bedgraph", SearchOption.AllDirectories).ShouldNotBeEmpty("the results still arrive, in the fresh folder");
    }

    [Fact]
    public async Task A_row_whose_folder_carries_another_jobs_marker_is_not_written_into()
    {
        var (env, victim) = await SeedRowNamingAsync("job-1043-b", prepare: v => RunFolderMarker.Write(v, "another-job"));

        var outcome = (await env.Reconciler().ReattachAsync(CancellationToken.None)).Single(o => o.JobId == "job-1043-b");

        outcome.FinalPhase.ShouldBe(JobPhase.Completed, outcome.ErrorCode);
        Entries(victim).ShouldBe([RunFolderMarker.FileName], "only the other job's own marker is there");
        RunFolderMarker.Check(victim, "another-job").ShouldBe(RunFolderMarkerState.Matches);
        env.Row("job-1043-b").OutputDir.ShouldNotBe(victim);
    }

    [Fact]
    public async Task A_row_whose_folder_carries_this_jobs_marker_is_resumed_into()
    {
        var (env, victim) = await SeedRowNamingAsync("job-1043-c", prepare: v => RunFolderMarker.Write(v, "job-1043-c"));

        var outcome = (await env.Reconciler().ReattachAsync(CancellationToken.None)).Single(o => o.JobId == "job-1043-c");

        outcome.FinalPhase.ShouldBe(JobPhase.Completed, outcome.ErrorCode);
        env.Row("job-1043-c").OutputDir.ShouldBe(victim, "a folder this run made is the one it resumes into");
        Directory.EnumerateFiles(victim, "*.bedgraph", SearchOption.AllDirectories).ShouldNotBeEmpty();
    }
}
