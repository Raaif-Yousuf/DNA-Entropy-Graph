using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Persistence.Tests;

public class RunRepositoryTests
{
    [Fact]
    public async Task Upserted_run_is_returned_by_get_all()
    {
        using var paths = new TempPaths();
        var repository = new RunRepository(new SqliteDatabase(paths.DatabasePath));
        var run = new RunRecord("job-1", JobPhase.Running, DateTimeOffset.UtcNow);

        await repository.UpsertAsync(run, CancellationToken.None);
        var all = await repository.GetAllAsync(CancellationToken.None);

        all.ShouldContain(r => r.JobId == "job-1" && r.Phase == JobPhase.Running);
    }

    [Fact]
    public async Task Upserting_the_same_job_id_again_replaces_it_rather_than_duplicating()
    {
        using var paths = new TempPaths();
        var repository = new RunRepository(new SqliteDatabase(paths.DatabasePath));
        await repository.UpsertAsync(new RunRecord("job-2", JobPhase.Running, DateTimeOffset.UtcNow), CancellationToken.None);
        await repository.UpsertAsync(new RunRecord("job-2", JobPhase.Completed, DateTimeOffset.UtcNow), CancellationToken.None);

        var all = await repository.GetAllAsync(CancellationToken.None);

        all.Count(r => r.JobId == "job-2").ShouldBe(1);
        all.Single(r => r.JobId == "job-2").Phase.ShouldBe(JobPhase.Completed);
    }

    [Fact]
    public async Task Empty_database_returns_an_empty_list_rather_than_throwing()
    {
        using var paths = new TempPaths();
        var repository = new RunRepository(new SqliteDatabase(paths.DatabasePath));

        var all = await repository.GetAllAsync(CancellationToken.None);

        all.ShouldBeEmpty();
    }

    /// <summary>
    /// This is issue #67's own "Observable that proves it is wired": kill the
    /// app immediately after pressing Run, relaunch, and the run row is still
    /// there. A brand-new <see cref="SqliteDatabase"/> instance pointed at
    /// the same file stands in for the relaunch - if this used an in-memory
    /// or per-instance store, the second instance would see nothing.
    /// </summary>
    [Fact]
    public async Task A_run_survives_a_relaunch_a_fresh_SqliteDatabase_pointed_at_the_same_file_still_sees_it()
    {
        using var paths = new TempPaths();
        var firstLaunch = new RunRepository(new SqliteDatabase(paths.DatabasePath));
        await firstLaunch.UpsertAsync(
            new RunRecord("job-relaunch", JobPhase.Validating, DateTimeOffset.UtcNow, Name: "SetTnpB-Evo"),
            CancellationToken.None);

        // A brand-new SqliteDatabase + RunRepository, exactly as App.xaml.cs
        // would construct on the next process start - no shared in-memory
        // state with firstLaunch is possible here.
        var secondLaunch = new RunRepository(new SqliteDatabase(paths.DatabasePath));
        var all = await secondLaunch.GetAllAsync(CancellationToken.None);

        var row = all.ShouldHaveSingleItem();
        row.JobId.ShouldBe("job-relaunch");
        row.Phase.ShouldBe(JobPhase.Validating);
        row.Name.ShouldBe("SetTnpB-Evo");
    }

    [Fact]
    public async Task Every_extended_column_round_trips()
    {
        using var paths = new TempPaths();
        var repository = new RunRepository(new SqliteDatabase(paths.DatabasePath));
        var createdAt = DateTimeOffset.Parse("2026-09-19T14:22:33Z");
        var startedAt = createdAt.AddSeconds(2);
        var run = new RunRecord(
            JobId: "job-full",
            Phase: JobPhase.Failed,
            CreatedUtc: createdAt,
            Name: "My run",
            IsBatch: true,
            Target: "local",
            ErrorCode: "VM_BOOT_TIMEOUT",
            ErrorDetail: "no heartbeat within boot deadline",
            StartedAt: startedAt,
            OptionsJson: """{"outputs":["bedgraph"]}""",
            VmName: "deg-job-full",
            Zone: "us-central1-a",
            MachineType: "g2-standard-8",
            GpuType: "L4",
            IsSpot: false,
            VmReused: true,
            LastProgressSeq: 42,
            EstimatedCostUsd: 1.23,
            ActualCostUsd: 1.10,
            VmSeconds: 900,
            CloudResultsDeleted: true,
            AppVersion: "1.0.0",
            WorkerVersion: "1.0.0",
            ContractVersion: 1,
            InstallationId: "01H9X5G6K7M8N9P0Q1R2S3T4U5",
            Imported: false);

        await repository.UpsertAsync(run, CancellationToken.None);
        var roundTripped = (await repository.GetAllAsync(CancellationToken.None)).Single();

        roundTripped.ShouldBe(run);
    }

    /// <summary>
    /// The DDL comment calls out CreatedAt as a value that should never move
    /// once written; the UpsertSql deliberately leaves it out of its
    /// ON CONFLICT ... DO UPDATE SET list. Mutation-tested below (see this
    /// lane's final report).
    /// </summary>
    [Fact]
    public async Task CreatedUtc_is_preserved_across_an_update_even_if_a_caller_passes_a_different_value()
    {
        using var paths = new TempPaths();
        var repository = new RunRepository(new SqliteDatabase(paths.DatabasePath));
        var originalCreated = DateTimeOffset.Parse("2026-09-19T00:00:00Z");
        await repository.UpsertAsync(new RunRecord("job-created", JobPhase.Validating, originalCreated), CancellationToken.None);

        var laterCreated = originalCreated.AddDays(1);
        await repository.UpsertAsync(new RunRecord("job-created", JobPhase.Completed, laterCreated), CancellationToken.None);

        var row = (await repository.GetAllAsync(CancellationToken.None)).Single(r => r.JobId == "job-created");
        row.CreatedUtc.ShouldBe(originalCreated);
    }

    /// <summary>
    /// Issue #141's future columns (added to the DDL tonight - see
    /// Migrations/0001_initial.sql's comment). A phase-only Upsert (what
    /// JobEngine does on every transition) must never erase a note the user
    /// already typed - the same "immutable unless this call means to change
    /// it" rule as CreatedUtc, just for a different column.
    /// </summary>
    [Fact]
    public async Task Notes_and_tags_round_trip_and_survive_a_phase_only_upsert()
    {
        using var paths = new TempPaths();
        var repository = new RunRepository(new SqliteDatabase(paths.DatabasePath));
        await repository.UpsertAsync(
            new RunRecord("job-notes", JobPhase.Validating, DateTimeOffset.UtcNow, Notes: "check this later", TagsJson: """["important"]"""),
            CancellationToken.None);

        // JobEngine advancing the phase does not know about the note and
        // passes the RunRecord default (Notes: null) - that must not wipe it.
        await repository.UpsertAsync(new RunRecord("job-notes", JobPhase.Completed, DateTimeOffset.UtcNow), CancellationToken.None);

        var row = (await repository.GetAllAsync(CancellationToken.None)).Single(r => r.JobId == "job-notes");
        row.Notes.ShouldBe("check this later");
        row.TagsJson.ShouldBe("""["important"]""");
        row.Phase.ShouldBe(JobPhase.Completed);
    }
}
