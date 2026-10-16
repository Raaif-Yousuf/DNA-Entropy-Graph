using System.Collections.Concurrent;
using CommunityToolkit.Mvvm.Messaging;
using DnaEntropyGraph.Cloud;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Cost;
using DnaEntropyGraph.Core.Inputs;
using DnaEntropyGraph.Core.LocalEngine;
using DnaEntropyGraph.LocalEngine;
using DnaEntropyGraph.Persistence;
using DnaEntropyGraph.Presentation.Messaging;
using DnaEntropyGraph.Presentation.Services;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.App.UiTests;

/// <summary>
/// Issue #109, through the real start path: the real <see cref="JobEngine"/>, the real <see cref="CloudJobRunner"/> on
/// <see cref="FakeGcp"/>, the real <see cref="SpendLedger"/> behind the run repository decorator the app registers.
/// </summary>
public class JobEngineSpendCapTests
{
    private const string Project = "my-project";

    private const string PricingJson = """
        {"schema":1,"asOf":"2026-10-01","currency":"USD","region":"us-central1","source":"test",
         "disk":{"usdPerGbMonth":0.073},
         "machines":{"g2-standard-8":{"accelerator":"L4","onDemandUsdPerHour":1.0,"spotUsdPerHour":0.4}}}
        """;

    private static readonly string Pinned = "ghcr.io/raaif-yousuf/dna-entropy-worker:0.1.0-cuda@sha256:" + new string('a', 64);

    private static readonly string TempRoot = Path.Combine(Path.GetTempPath(), "deg-spend-tests-" + Guid.NewGuid().ToString("N"));

    private sealed class Image : IWorkerImageProvider
    {
        public WorkerImageResolution Resolve(string appVersion, bool gpu) => new(WorkerImageStatus.Available, Pinned);
    }

    private sealed class Pricing(string json) : IPricingSource
    {
        public PricingLoadResult Load() => PricingTable.Parse(json);
    }

    private sealed class MemorySettings : ISettingsStore
    {
        private readonly ConcurrentDictionary<string, string> _values = new();

        public string? GetString(string key) => _values.TryGetValue(key, out var v) ? v : null;

        public void SetString(string key, string value) => _values[key] = value;
    }

    private sealed class MemoryRuns : IRunRepository
    {
        private readonly ConcurrentDictionary<string, RunRecord> _rows = new();

        public Task<IReadOnlyList<RunRecord>> GetAllAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<RunRecord>>(_rows.Values.ToList());

        public Task UpsertAsync(RunRecord run, CancellationToken cancellationToken)
        {
            _rows[run.JobId] = run;
            return Task.CompletedTask;
        }
    }

    private sealed class MemoryProjects : IProjectRepository
    {
        public Task<IReadOnlyList<ProjectRecord>> GetAllAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ProjectRecord>>([]);

        public Task UpsertAsync(ProjectRecord project, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task EnsureAsync(string projectId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class MemoryLedger : ICostLedgerRepository
    {
        private readonly List<CostLedgerEntry> _rows = [];

        public Task<IReadOnlyList<CostLedgerEntry>> GetAllAsync(CancellationToken cancellationToken)
        {
            lock (_rows)
            {
                return Task.FromResult<IReadOnlyList<CostLedgerEntry>>(_rows.ToList());
            }
        }

        public Task<CostLedgerEntry?> TryAppendAsync(CostLedgerEntry entry, CancellationToken cancellationToken)
        {
            lock (_rows)
            {
                if (entry.RunId is not null && _rows.Any(r => r.RunId == entry.RunId && r.Kind == entry.Kind))
                {
                    return Task.FromResult<CostLedgerEntry?>(null);
                }

                var stored = entry with { Id = _rows.Count + 1 };
                _rows.Add(stored);
                return Task.FromResult<CostLedgerEntry?>(stored);
            }
        }

        public Task<CostLedgerEntry> AppendAsync(CostLedgerEntry entry, CancellationToken cancellationToken)
        {
            lock (_rows)
            {
                var stored = entry with { Id = _rows.Count + 1 };
                _rows.Add(stored);
                return Task.FromResult(stored);
            }
        }
    }

    private sealed class NoLocalReadiness : ILocalReadinessSource
    {
        public Task<LocalReadiness> CheckAsync(CancellationToken cancellationToken)
            => Task.FromResult(new LocalReadiness(new LocalCapability(false, [LocalBlocker.NoNvidiaGpu], null, 0), LocalEngineHealth.NotInstalled()));
    }

    private sealed class NoProcessLauncher : IProcessLauncher
    {
        public IRunningProcess Start(ProcessSpec spec, Action<string>? onOutputLine = null) => throw new ProcessStartFailedException(spec.FileName);
    }

    private sealed class Harness
    {
        public Harness(FakeGcp gcp, string? pricingJson = null)
        {
            Gcp = gcp;
            Settings = new MemorySettings();
            Ledger = new MemoryLedger();
            Inner = new MemoryRuns();
            var pricing = new Pricing(pricingJson ?? PricingJson);
            Gate = new SpendLedger(Ledger, pricing, Settings, TimeProvider.System, TimeZoneInfo.Local, Inner);
            var messenger = new WeakReferenceMessenger();
            Runs = new LedgerRecordingRunRepository(Inner, Gate);
            messenger.Register<Harness, RunPhaseChangedMessage>(this, (_, m) =>
            {
                if (JobStateMachine.IsTerminal(m.Phase))
                {
                    Terminal.TrySetResult(m.Phase);
                }
            });
            var runner = new CloudJobRunner(gcp, gcp, gcp, gcp, gcp, Runs, (id, phase) => messenger.Send(new RunPhaseChangedMessage(id, phase)))
            {
                ResultPollInterval = TimeSpan.FromMilliseconds(1),
                ResultTimeout = TimeSpan.FromSeconds(5),
            };
            var appData = Path.Combine(TempRoot, "appdata-" + Guid.NewGuid().ToString("N"));
            var inputs = new LocalRunInputStore(appData);
            var activeRuns = new ActiveRuns();
            var ready = new NoLocalReadiness();
            var localRunner = new LocalJobRunner(
                new NoProcessLauncher(),
                Runs,
                new LocalEngineLayout(Path.Combine(appData, "engine")),
                Path.Combine(appData, "runs"),
                (id, phase) => messenger.Send(new RunPhaseChangedMessage(id, phase)));
            var local = new LocalRunDriver(messenger, localRunner, ready, Settings, Runs, inputs, activeRuns);
            Engine = new JobEngine(
                messenger, runner, gcp, Settings, Runs, new MemoryProjects(), inputs,
                new Image(), new ScriptedAccessionLookup(), activeRuns, new RunTargetResolver(ready), local,
                estimates: new CostEstimateService(pricing, Inner, Gate));
        }

        public FakeGcp Gcp { get; }

        public MemorySettings Settings { get; }

        public MemoryLedger Ledger { get; }

        public SpendLedger Gate { get; }

        public MemoryRuns Inner { get; }

        public IRunRepository Runs { get; }

        public JobEngine Engine { get; }

        public TaskCompletionSource<JobPhase> Terminal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<JobPhase> TerminalAsync() => await Terminal.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static RunOptions Options()
    {
        var dir = Path.Combine(TempRoot, "user");
        Directory.CreateDirectory(dir);
        var input = Path.Combine(dir, "SetTnpB.gb");
        File.WriteAllText(input, "LOCUS       SetTnpB\nORIGIN\n        1 acgtacgtac\n//\n");
        return new RunOptions
        {
            ModelId = "evo2_7b",
            RunTarget = "Cloud",
            AfterTask = AfterTaskAction.Stop,
            InputPath = input,
            OutputFolder = Path.Combine(TempRoot, "out-" + Guid.NewGuid().ToString("N")),
        };
    }

    [Fact]
    public async Task A_completed_run_writes_exactly_one_ledger_row_for_its_job_and_marks_the_run_row()
    {
        var h = new Harness(new FakeGcp().WithSelectedProject(Project));

        var jobId = await h.Engine.StartRunAsync(Options(), CancellationToken.None);
        (await h.TerminalAsync()).ShouldBe(JobPhase.Completed);

        var rows = await h.Ledger.GetAllAsync(CancellationToken.None);
        var row = rows.Single();
        row.RunId.ShouldBe(jobId);
        row.Kind.ShouldBe("vm_runtime");
        row.UsdEst.ShouldNotBeNull().ShouldBeGreaterThan(0, "the VM existed for a moment, so the booked cost is not zero");
        row.StartedUtc.ShouldNotBe(default, "booked from the VM's own start");
        var run = (await h.Inner.GetAllAsync(CancellationToken.None)).Single(r => r.JobId == jobId);
        run.VmReadyAt.ShouldNotBeNull("the runner records when the VM was known to exist, the start of what is booked");
        row.StartedUtc.ShouldBe(run.VmReadyAt!.Value);
        run.EstimatedCostUsd.ShouldBe(row.UsdEst);
        run.VmSeconds.ShouldNotBeNull();
    }

    [Fact]
    public async Task The_Results_header_and_the_Runs_row_show_the_figure_the_ledger_booked_for_a_completed_run()
    {
        // Issues #100 and #557: one figure, booked once by the ledger decorator, read by both pages through the real view models.
        var h = new Harness(new FakeGcp().WithSelectedProject(Project));
        var jobId = await h.Engine.StartRunAsync(Options(), CancellationToken.None);
        (await h.TerminalAsync()).ShouldBe(JobPhase.Completed);
        var booked = (await h.Ledger.GetAllAsync(CancellationToken.None)).Single().UsdEst.ShouldNotBeNull();
        var amount = "$" + RunCostText.Money(booked);
        var run = (await h.Inner.GetAllAsync(CancellationToken.None)).Single(r => r.JobId == jobId);
        var strings = new EchoStrings();

        var header = RunCostText.Header(run, strings); 
        var row = RunCostText.Short(run, strings);

        header.ShouldNotBeNull().ShouldContain(amount);
        row.ShouldNotBeNull().ShouldContain(amount);
    }

    private sealed class EchoStrings : IStringResourceProvider
    {
        public string GetString(string key) => key + " ${0}";
    }

    [Fact]
    public async Task With_the_hard_stop_on_and_the_cap_below_the_months_spend_the_run_is_refused_with_SPEND_CAP_and_nothing_is_created()
    {
        var h = new Harness(new FakeGcp().WithSelectedProject(Project));
        await h.Ledger.AppendAsync(new CostLedgerEntry("vm_runtime", DateTimeOffset.UtcNow, "earlier", EndedUtc: DateTimeOffset.UtcNow, UsdEst: 12), CancellationToken.None);
        h.Settings.SetString(SpendCapSettings.CapUsdKey, "10");
        h.Settings.SetString(SpendCapSettings.HardStopKey, "true");

        var jobId = await h.Engine.StartRunAsync(Options(), CancellationToken.None);

        (await h.TerminalAsync()).ShouldBe(JobPhase.Failed);
        var run = (await h.Inner.GetAllAsync(CancellationToken.None)).Single(r => r.JobId == jobId);
        run.ErrorCode.ShouldBe(RunErrorCodes.SpendCap);
        h.Gcp.CreatedSpecs.ShouldBeEmpty();
        (await h.Gcp.FindByJobIdAsync(jobId, CancellationToken.None)).ShouldBeEmpty();
        (await h.Ledger.GetAllAsync(CancellationToken.None)).Count.ShouldBe(1, "a refused run costs nothing and writes no row");
    }

    [Fact]
    public async Task The_same_overage_with_the_hard_stop_off_only_warns_and_the_run_starts()
    {
        var h = new Harness(new FakeGcp().WithSelectedProject(Project));
        await h.Ledger.AppendAsync(new CostLedgerEntry("vm_runtime", DateTimeOffset.UtcNow, "earlier", EndedUtc: DateTimeOffset.UtcNow, UsdEst: 12), CancellationToken.None);
        h.Settings.SetString(SpendCapSettings.CapUsdKey, "10");
        (await h.Gate.CheckAsync(null, CancellationToken.None)).Level.ShouldBe(SpendCapLevel.Warning, "the cap is read, so the only thing that lets the run start is that the hard stop is off");

        await h.Engine.StartRunAsync(Options(), CancellationToken.None);

        (await h.TerminalAsync()).ShouldBe(JobPhase.Completed);
        h.Gcp.CreatedSpecs.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_run_whose_own_estimate_would_reach_the_cap_is_refused_though_the_months_spend_alone_is_under_it()
    {
        // Cap 10, month so far 6, and a price of 40 an hour makes this run's own estimate about 8: the card says "will not start".
        var expensive = PricingJson.Replace("\"onDemandUsdPerHour\":1.0", "\"onDemandUsdPerHour\":40.0", StringComparison.Ordinal).Replace("\"spotUsdPerHour\":0.4", "\"spotUsdPerHour\":20.0", StringComparison.Ordinal);
        var h = new Harness(new FakeGcp().WithSelectedProject(Project), expensive);
        await h.Ledger.AppendAsync(new CostLedgerEntry("vm_runtime", DateTimeOffset.UtcNow, "earlier", EndedUtc: DateTimeOffset.UtcNow, UsdEst: 6), CancellationToken.None);
        h.Settings.SetString(SpendCapSettings.CapUsdKey, "10");
        h.Settings.SetString(SpendCapSettings.HardStopKey, "true");

        var jobId = await h.Engine.StartRunAsync(Options(), CancellationToken.None);

        (await h.TerminalAsync()).ShouldBe(JobPhase.Failed);
        (await h.Inner.GetAllAsync(CancellationToken.None)).Single(r => r.JobId == jobId).ErrorCode.ShouldBe(RunErrorCodes.SpendCap);
        h.Gcp.CreatedSpecs.ShouldBeEmpty();
    }

    // The run's own estimate is its MaxUsd: 12 minutes (a fresh computer) at the machine's rate plus the disk, so about 0.2 * (rate + 0.015).
    // With 6 already spent and a cap of 10, a rate of 19.5 estimates about 3.9 (6 + 3.9 stays under 10) and 20.5 about 4.1 (reaches it).
    [Theory]
    [InlineData(19.5, true)]
    [InlineData(20.5, false)]
    public async Task A_price_just_under_the_cap_starts_and_one_just_over_is_refused_so_the_estimate_is_what_decides(double rate, bool starts)
    {
        var priced = PricingJson
            .Replace("\"onDemandUsdPerHour\":1.0", FormattableString.Invariant($"\"onDemandUsdPerHour\":{rate}"), StringComparison.Ordinal)
            .Replace("\"spotUsdPerHour\":0.4", FormattableString.Invariant($"\"spotUsdPerHour\":{rate / 2}"), StringComparison.Ordinal);
        var h = new Harness(new FakeGcp().WithSelectedProject(Project), priced);
        await h.Ledger.AppendAsync(new CostLedgerEntry("vm_runtime", DateTimeOffset.UtcNow, "earlier", EndedUtc: DateTimeOffset.UtcNow, UsdEst: 6), CancellationToken.None);
        h.Settings.SetString(SpendCapSettings.CapUsdKey, "10");
        h.Settings.SetString(SpendCapSettings.HardStopKey, "true");

        await h.Engine.StartRunAsync(Options(), CancellationToken.None);

        (await h.TerminalAsync()).ShouldBe(starts ? JobPhase.Completed : JobPhase.Failed);
        h.Gcp.CreatedSpecs.Count.ShouldBe(starts ? 1 : 0);
    }

    [Fact]
    public async Task The_same_run_at_a_price_that_stays_under_the_cap_starts()
    {
        var h = new Harness(new FakeGcp().WithSelectedProject(Project));
        await h.Ledger.AppendAsync(new CostLedgerEntry("vm_runtime", DateTimeOffset.UtcNow, "earlier", EndedUtc: DateTimeOffset.UtcNow, UsdEst: 6), CancellationToken.None);
        h.Settings.SetString(SpendCapSettings.CapUsdKey, "10");
        h.Settings.SetString(SpendCapSettings.HardStopKey, "true");

        await h.Engine.StartRunAsync(Options(), CancellationToken.None);

        (await h.TerminalAsync()).ShouldBe(JobPhase.Completed);
    }
}
