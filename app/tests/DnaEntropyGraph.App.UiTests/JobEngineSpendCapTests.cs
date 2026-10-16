using System.Collections.Concurrent;
using CommunityToolkit.Mvvm.Messaging;
using DnaEntropyGraph.Cloud;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Cost;
using DnaEntropyGraph.Core.Inputs;
using DnaEntropyGraph.Persistence;
using DnaEntropyGraph.Presentation.Messaging;
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

    private sealed class Pricing : IPricingSource
    {
        public PricingLoadResult Load() => PricingTable.Parse(PricingJson);
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

    private sealed class Harness
    {
        public Harness(FakeGcp gcp)
        {
            Gcp = gcp;
            Settings = new MemorySettings();
            Ledger = new MemoryLedger();
            Gate = new SpendLedger(Ledger, new Pricing(), Settings, TimeProvider.System, TimeZoneInfo.Local);
            var messenger = new WeakReferenceMessenger();
            Inner = new MemoryRuns();
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
            Engine = new JobEngine(
                messenger, runner, gcp, Settings, Runs, new MemoryProjects(),
                new LocalRunInputStore(Path.Combine(TempRoot, "appdata-" + Guid.NewGuid().ToString("N"))),
                new Image(), new ScriptedAccessionLookup(), new ActiveRuns(), spendGate: Gate);
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
        row.UsdEst.ShouldNotBeNull();
        var run = (await h.Inner.GetAllAsync(CancellationToken.None)).Single(r => r.JobId == jobId);
        run.EstimatedCostUsd.ShouldBe(row.UsdEst);
        run.VmSeconds.ShouldNotBeNull();
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

        await h.Engine.StartRunAsync(Options(), CancellationToken.None);

        (await h.TerminalAsync()).ShouldBe(JobPhase.Completed);
        h.Gcp.CreatedSpecs.Count.ShouldBe(1);
    }
}
