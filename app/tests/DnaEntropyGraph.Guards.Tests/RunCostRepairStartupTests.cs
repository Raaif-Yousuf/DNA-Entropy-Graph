using DnaEntropyGraph.App.Startup;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// Issues #100 and #557, wired-to-nothing: reads never write. The one place a ledger append that failed after its run row was stored is booked is the repair
/// <see cref="AppStartup.BeginAsync"/> starts in the background. These drive the real container: a repair method that exists but is never called, or a page read that
/// quietly books again, both turn one of them red.
/// </summary>
public class RunCostRepairStartupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "deg-runcost-repair-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // SQLite may still hold the file for a moment; the temp folder is not worth failing a test over.
        }
    }

    /// <summary>A real SQLite ledger that signals every append attempt and can be made to fail every read of the run rows' entries.</summary>
    private sealed class WatchedLedger(ICostLedgerRepository inner) : ICostLedgerRepository
    {
        private readonly TaskCompletionSource _appended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _appendAttempts;

        public bool FailReads { get; set; }

        public int AppendAttempts => Volatile.Read(ref _appendAttempts);

        /// <summary>Completes when an append is attempted (a signal, so no test polls on a real clock).</summary>
        public Task Appended => _appended.Task;

        public Task<IReadOnlyList<CostLedgerEntry>> GetAllAsync(CancellationToken cancellationToken) => inner.GetAllAsync(cancellationToken);

        public Task<IReadOnlyList<CostLedgerEntry>> GetByKindAsync(string kind, CancellationToken cancellationToken)
            => FailReads ? throw new IOException("database is locked") : inner.GetByKindAsync(kind, cancellationToken);

        public Task<CostLedgerEntry> AppendAsync(CostLedgerEntry entry, CancellationToken cancellationToken) => inner.AppendAsync(entry, cancellationToken);

        public async Task<CostLedgerEntry?> TryAppendAsync(CostLedgerEntry entry, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _appendAttempts);
            var written = await inner.TryAppendAsync(entry, cancellationToken).ConfigureAwait(false);
            _appended.TrySetResult();
            return written;
        }
    }

    private sealed class RecordingLog : IDiagnosticsLog
    {
        private readonly TaskCompletionSource<(string Source, string? JobId, string ErrorClass)> _first = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<(string Source, string? JobId, string ErrorClass)> First => _first.Task;

        public void Warning(string source, string? jobId, string errorClass)
        {
            if (source == "run-cost-repair")
            {
                _first.TrySetResult((source, jobId, errorClass));
            }
        }
    }

    /// <summary>
    /// A counting seam on the repair the launch calls: how many times it was called, and the ledger's row count the instant before each call started (so a booking made
    /// by any OTHER startup path shows up as a row that was already there) and the instant after it ended.
    /// </summary>
    private sealed class CountingRepair(IRunCostRepair inner, WatchedLedger ledger) : IRunCostRepair
    {
        public List<(int Before, int After)> Calls { get; } = [];

        public async Task BookUnbookedRunsAsync(CancellationToken cancellationToken)
        {
            var before = (await ledger.GetAllAsync(cancellationToken).ConfigureAwait(false)).Count;
            await inner.BookUnbookedRunsAsync(cancellationToken).ConfigureAwait(false);
            Calls.Add((before, (await ledger.GetAllAsync(cancellationToken).ConfigureAwait(false)).Count));
        }
    }

    private ServiceProvider Build(WatchedLedger[] ledger, IDiagnosticsLog? log = null, CountingRepair[]? repair = null)
    {
        var services = new ServiceCollection();
        services.AddDnaEntropyGraph(appDataRoot: _root, cloud: CloudBackend.Fake);
        services.AddSingleton<ICostLedgerRepository>(sp => ledger[0] = new WatchedLedger(new CostLedgerRepository(sp.GetRequiredService<SqliteDatabase>())));
        if (repair is not null)
        {
            services.AddSingleton<IRunCostRepair>(sp => repair[0] = new CountingRepair(sp.GetRequiredService<SpendLedger>(), (WatchedLedger)sp.GetRequiredService<ICostLedgerRepository>()));
        }

        if (log is not null)
        {
            services.AddSingleton<IDiagnosticsLog>(log);
        }

        var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<ICostLedgerRepository>(); // singletons are lazy: materialise the watched ledger so the test can reach it
        return provider;
    }

    /// <summary>An ended cloud run whose ledger append failed: written through the UNDECORATED repository, so no booking happens on the write.</summary>
    private static Task SeedUnbookedRunAsync(ServiceProvider provider, string jobId)
    {
        var now = DateTimeOffset.UtcNow;
        return provider.GetRequiredService<RunRepository>().UpsertAsync(
            new RunRecord(jobId, JobPhase.Completed, now.AddHours(-3), Target: "cloud", MachineType: "g2-standard-8", VmReadyAt: now.AddHours(-2), FinishedAt: now.AddHours(-1)),
            CancellationToken.None);
    }

    [Fact]
    public async Task Reading_the_run_cost_through_the_real_container_never_books_an_unbooked_run()
    {
        var ledger = new WatchedLedger[1];
        using var provider = Build(ledger);
        await SeedUnbookedRunAsync(provider, "unbooked-1");

        var booked = await provider.GetRequiredService<IRunCostSource>().GetBookedAsync(TestContext.Current.CancellationToken);

        booked.ShouldBeEmpty("the run's append failed; a read does not repair it");
        ledger[0].AppendAttempts.ShouldBe(0, "a page, Results or export read made a ledger write");
        (await ledger[0].GetAllAsync(TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    [Fact]
    public async Task The_launch_entry_books_a_run_whose_ledger_append_failed_exactly_once()
    {
        var ledger = new WatchedLedger[1];
        using var provider = Build(ledger);
        await SeedUnbookedRunAsync(provider, "unbooked-2");

        await AppStartup.BeginAsync(provider, TestContext.Current.CancellationToken);
        await ledger[0].Appended.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        var entries = await ledger[0].GetAllAsync(TestContext.Current.CancellationToken);
        entries.Count(e => e.RunId == "unbooked-2" && e.Kind == SpendLedger.VmRuntimeKind).ShouldBe(1, "the startup repair is the one writer of a missed booking");

        // A second launch finds it booked: idempotent, one row.
        await AppStartup.BeginAsync(provider, TestContext.Current.CancellationToken);
        (await provider.GetRequiredService<SpendLedger>().MonthToDateAsync(TestContext.Current.CancellationToken)).RecordedUsd.ShouldBeGreaterThan(0);
        (await ledger[0].GetAllAsync(TestContext.Current.CancellationToken)).Count(e => e.RunId == "unbooked-2").ShouldBe(1);
    }

    [Fact]
    public async Task Each_launch_runs_the_repair_exactly_once_and_the_booking_comes_from_it()
    {
        var ledger = new WatchedLedger[1];
        var repair = new CountingRepair[1];
        using var provider = Build(ledger, repair: repair);
        _ = provider.GetRequiredService<IRunCostRepair>(); // materialise the seam
        await SeedUnbookedRunAsync(provider, "unbooked-4");

        await AppStartup.BeginAsync(provider, TestContext.Current.CancellationToken); // ends only after the background repair has

        repair[0].Calls.Count.ShouldBe(1, "one repair per launch: none means it is wired to nothing, two means it is duplicated");
        repair[0].Calls[0].Before.ShouldBe(0, "no other startup path booked the run before the repair looked");
        repair[0].Calls[0].After.ShouldBe(1, "the repair is what booked it");

        await AppStartup.BeginAsync(provider, TestContext.Current.CancellationToken);

        repair[0].Calls.Count.ShouldBe(2, "the second launch ran it once more");
        repair[0].Calls[1].ShouldBe((1, 1), "and found nothing left to book");
    }

    [Fact]
    public async Task A_launch_cancelled_before_the_repair_ends_leaves_no_unobserved_fault()
    {
        var ledger = new WatchedLedger[1];
        using var provider = Build(ledger);
        await SeedUnbookedRunAsync(provider, "unbooked-5");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var faulted = 0;
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e) => Interlocked.Increment(ref faulted);
        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            await Record.ExceptionAsync(() => AppStartup.BeginAsync(provider, cts.Token)); // a cancelled launch may itself end cancelled; only the repair's fault matters here
            for (var i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }

        faulted.ShouldBe(0, "a cancelled repair is a requested stop, not a fault nobody observes");
    }

    [Fact]
    public async Task A_startup_repair_that_fails_is_logged_with_its_error_class_and_does_not_crash_the_launch()
    {
        var ledger = new WatchedLedger[1];
        var log = new RecordingLog();
        using var provider = Build(ledger, log);
        await SeedUnbookedRunAsync(provider, "unbooked-3");
        ledger[0].FailReads = true;

        await Should.NotThrowAsync(() => AppStartup.BeginAsync(provider, TestContext.Current.CancellationToken));

        var (source, jobId, errorClass) = await log.First.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        source.ShouldBe("run-cost-repair");
        jobId.ShouldBeNull();
        errorClass.ShouldBe("IOException", "the error class only: never the message, which could carry a path");
    }
}
