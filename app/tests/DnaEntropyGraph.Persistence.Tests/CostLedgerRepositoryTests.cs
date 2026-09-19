using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Persistence.Tests;

public class CostLedgerRepositoryTests
{
    [Fact]
    public async Task Appended_entry_is_returned_by_get_all_with_a_generated_id()
    {
        using var paths = new TempPaths();
        var repository = new CostLedgerRepository(new SqliteDatabase(paths.DatabasePath));
        var entry = new CostLedgerEntry("vm_runtime", DateTimeOffset.UtcNow, UsdEst: 0.42);

        var appended = await repository.AppendAsync(entry, CancellationToken.None);

        appended.Id.ShouldNotBe(0L);
        var all = await repository.GetAllAsync(CancellationToken.None);
        all.ShouldContain(e => e.Id == appended.Id && e.Kind == "vm_runtime" && e.UsdEst == 0.42);
    }

    [Fact]
    public async Task Appending_twice_never_overwrites_the_first_entry_a_ledger_is_append_only()
    {
        using var paths = new TempPaths();
        var repository = new CostLedgerRepository(new SqliteDatabase(paths.DatabasePath));

        var first = await repository.AppendAsync(new CostLedgerEntry("vm_runtime", DateTimeOffset.UtcNow, UsdEst: 1.0), CancellationToken.None);
        var second = await repository.AppendAsync(new CostLedgerEntry("storage", DateTimeOffset.UtcNow, UsdEst: 2.0), CancellationToken.None);

        first.Id.ShouldNotBe(second.Id);
        var all = await repository.GetAllAsync(CancellationToken.None);
        all.Count.ShouldBe(2);
    }

    [Fact]
    public async Task An_entry_survives_a_relaunch()
    {
        using var paths = new TempPaths();
        var firstLaunch = new CostLedgerRepository(new SqliteDatabase(paths.DatabasePath));
        await firstLaunch.AppendAsync(new CostLedgerEntry("disk", DateTimeOffset.UtcNow, UsdEst: 0.05), CancellationToken.None);

        var secondLaunch = new CostLedgerRepository(new SqliteDatabase(paths.DatabasePath));
        var all = await secondLaunch.GetAllAsync(CancellationToken.None);

        all.ShouldContain(e => e.Kind == "disk");
    }
}
