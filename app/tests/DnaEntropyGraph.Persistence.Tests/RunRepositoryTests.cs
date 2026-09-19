using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Persistence;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Persistence.Tests;

public class RunRepositoryTests
{
    [Fact]
    public async Task Upserted_run_is_returned_by_get_all()
    {
        var repository = new RunRepository();
        var run = new RunRecord("job-1", JobPhase.Running, DateTimeOffset.UtcNow);

        await repository.UpsertAsync(run, CancellationToken.None);
        var all = await repository.GetAllAsync(CancellationToken.None);

        all.ShouldContain(r => r.JobId == "job-1" && r.Phase == JobPhase.Running);
    }

    [Fact]
    public async Task Upserting_the_same_job_id_again_replaces_it_rather_than_duplicating()
    {
        var repository = new RunRepository();
        await repository.UpsertAsync(new RunRecord("job-2", JobPhase.Running, DateTimeOffset.UtcNow), CancellationToken.None);
        await repository.UpsertAsync(new RunRecord("job-2", JobPhase.Completed, DateTimeOffset.UtcNow), CancellationToken.None);

        var all = await repository.GetAllAsync(CancellationToken.None);

        all.Count(r => r.JobId == "job-2").ShouldBe(1);
        all.Single(r => r.JobId == "job-2").Phase.ShouldBe(JobPhase.Completed);
    }
}

public class SettingsStoreTests
{
    [Fact]
    public void A_value_that_was_never_set_reads_back_null()
    {
        var store = new SettingsStore();

        store.GetString("Theme").ShouldBeNull();
    }

    [Fact]
    public void A_value_that_was_set_reads_back_the_same_value()
    {
        var store = new SettingsStore();

        store.SetString("Theme", "Dark");

        store.GetString("Theme").ShouldBe("Dark");
    }
}
