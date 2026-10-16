using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Persistence.Tests;

/// <summary>Issue #141: the notes and tags columns are written by their own method, touching nothing else.</summary>
public sealed class RunAnnotationsTests
{
    [Fact]
    public async Task Updating_notes_and_tags_writes_only_those_two_columns()
    {
        using var paths = new TempPaths();
        var repository = new RunRepository(new SqliteDatabase(paths.DatabasePath));
        await repository.UpsertAsync(new RunRecord("job-a", JobPhase.Completed, DateTimeOffset.UtcNow, Name: "n", Zone: "z1", PreemptionCount: 2, ErrorDetail: "d1"), CancellationToken.None);

        await ((IRunAnnotations)repository).UpdateAsync("job-a", "failed sequencing", """["clone7"]""", CancellationToken.None);

        var row = (await repository.GetAllAsync(CancellationToken.None)).Single();
        row.Notes.ShouldBe("failed sequencing");
        row.TagsJson.ShouldBe("""["clone7"]""");
        row.Phase.ShouldBe(JobPhase.Completed);
        row.Zone.ShouldBe("z1");
        row.PreemptionCount.ShouldBe(2);
        row.ErrorDetail.ShouldBe("d1");
    }

    [Fact]
    public async Task A_phase_write_after_a_note_edit_keeps_the_note_and_a_note_edit_after_a_phase_write_keeps_the_phase()
    {
        using var paths = new TempPaths();
        var repository = new RunRepository(new SqliteDatabase(paths.DatabasePath));
        IRunAnnotations annotations = repository;
        var created = DateTimeOffset.UtcNow;
        await repository.UpsertAsync(new RunRecord("job-b", JobPhase.Running, created), CancellationToken.None);
        await annotations.UpdateAsync("job-b", "typed while running", null, CancellationToken.None);

        await repository.UpsertAsync(new RunRecord("job-b", JobPhase.Completed, created), CancellationToken.None);
        (await repository.GetAllAsync(CancellationToken.None)).Single().Notes.ShouldBe("typed while running");

        await annotations.UpdateAsync("job-b", "edited", null, CancellationToken.None);
        var row = (await repository.GetAllAsync(CancellationToken.None)).Single();
        row.Phase.ShouldBe(JobPhase.Completed);
        row.Notes.ShouldBe("edited");
    }

    [Fact]
    public async Task Clearing_writes_null_and_an_unknown_run_is_not_an_error_and_creates_nothing()
    {
        using var paths = new TempPaths();
        var repository = new RunRepository(new SqliteDatabase(paths.DatabasePath));
        IRunAnnotations annotations = repository;
        await repository.UpsertAsync(new RunRecord("job-c", JobPhase.Completed, DateTimeOffset.UtcNow, Notes: "x", TagsJson: """["t"]"""), CancellationToken.None);
        (await annotations.UpdateAsync("job-c", null, null, CancellationToken.None)).ShouldBeTrue();
        (await annotations.UpdateAsync("ghost", "n", null, CancellationToken.None)).ShouldBeFalse();

        var all = await repository.GetAllAsync(CancellationToken.None);
        all.Count.ShouldBe(1);
        all[0].Notes.ShouldBeNull();
        all[0].TagsJson.ShouldBeNull();
    }
}
