using System.Collections.Concurrent;
using CommunityToolkit.Mvvm.Messaging;
using DnaEntropyGraph.Cloud;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Presentation.Messaging;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.App.UiTests;

/// <summary>
/// Issue #428. JobEngine is a plain class with no WinUI window, so these run
/// without an interactive session. They prove the seam ShellViewModel's
/// tests stop at: a run started through the real <see cref="JobEngine"/>
/// reaches <see cref="CloudJobRunner"/>, which reaches the gateway (FakeGcp
/// records the VM), and every phase the runner records reaches the messenger.
/// </summary>
public class JobEngineTests
{
    private const string Project = "my-project";

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

    private sealed class Harness
    {
        public Harness(FakeGcp gcp)
        {
            Gcp = gcp;
            Messenger = new WeakReferenceMessenger();
            Runs = new MemoryRuns();
            Settings = new MemorySettings();
            Messenger.Register<Harness, RunPhaseChangedMessage>(this, (_, m) =>
            {
                Messages.Enqueue(m);
                if (JobStateMachine.IsTerminal(m.Phase))
                {
                    Terminal.TrySetResult(m.Phase);
                }
            });
            var runner = new CloudJobRunner(gcp, gcp, gcp, gcp, Runs, (id, phase) => Messenger.Send(new RunPhaseChangedMessage(id, phase)));
            Engine = new JobEngine(Messenger, runner, gcp, Settings, Runs);
        }

        public FakeGcp Gcp { get; }

        public WeakReferenceMessenger Messenger { get; }

        public MemoryRuns Runs { get; }

        public MemorySettings Settings { get; }

        public JobEngine Engine { get; }

        public ConcurrentQueue<RunPhaseChangedMessage> Messages { get; } = new();

        public TaskCompletionSource<JobPhase> Terminal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<JobPhase> TerminalAsync() => await Terminal.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static RunOptions Options(string target = "Cloud", AfterTaskAction after = AfterTaskAction.Stop)
        => new() { ModelId = "evo2_7b", RunTarget = target, AfterTask = after };

    [Fact]
    public async Task Starting_a_cloud_run_reaches_the_gateway_and_completes()
    {
        var h = new Harness(new FakeGcp().WithSelectedProject(Project));

        var jobId = await h.Engine.StartRunAsync(Options(), CancellationToken.None);
        var terminal = await h.TerminalAsync();

        terminal.ShouldBe(JobPhase.Completed);
        h.Messages.Select(m => m.Phase).ShouldContain(JobPhase.Provisioning);
        h.Messages.ShouldAllBe(m => m.JobId == jobId);
        var vms = await h.Gcp.FindByJobIdAsync(jobId, CancellationToken.None);
        vms.Count.ShouldBe(1, "the observable: FakeGcp recorded a CreateVmAsync for this job id");
        vms[0].Status.ShouldBe("STOPPED", "Hard Rule 11 default after a run is Stop");
    }

    [Fact]
    public async Task The_run_row_is_written_up_front_and_keeps_its_columns()
    {
        var h = new Harness(new FakeGcp().WithSelectedProject(Project));

        var jobId = await h.Engine.StartRunAsync(Options(), CancellationToken.None);
        await h.TerminalAsync();

        var row = (await h.Runs.GetAllAsync(CancellationToken.None)).Single(r => r.JobId == jobId);
        row.Phase.ShouldBe(JobPhase.Completed);
        row.ProjectId.ShouldBe(Project);
        row.InstallationId.ShouldBe(InstallationId.GetOrCreate(h.Settings));
        row.OptionsJson.ShouldContain("evo2_7b");
        row.Target.ShouldBe("cloud");
    }

    [Fact]
    public async Task A_run_with_no_selected_project_fails_with_a_recorded_reason_and_creates_nothing()
    {
        var h = new Harness(new FakeGcp().WithSignedOut());

        var jobId = await h.Engine.StartRunAsync(Options(), CancellationToken.None);

        (await h.TerminalAsync()).ShouldBe(JobPhase.Failed);
        var row = (await h.Runs.GetAllAsync(CancellationToken.None)).Single(r => r.JobId == jobId);
        row.Phase.ShouldBe(JobPhase.Failed);
        row.ErrorCode.ShouldBe("no_project");
        (await h.Gcp.FindByJobIdAsync(jobId, CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_target_other_than_cloud_fails_visibly_instead_of_silently_running_in_the_cloud()
    {
        var h = new Harness(new FakeGcp().WithSelectedProject(Project));

        var jobId = await h.Engine.StartRunAsync(Options(target: "LocalPc"), CancellationToken.None);

        (await h.TerminalAsync()).ShouldBe(JobPhase.Failed);
        (await h.Runs.GetAllAsync(CancellationToken.None)).Single(r => r.JobId == jobId).ErrorCode.ShouldBe("target_not_supported");
        (await h.Gcp.FindByJobIdAsync(jobId, CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_configured_worker_image_reaches_the_VM_as_startup_metadata()
    {
        var h = new Harness(new FakeGcp().WithSelectedProject(Project));
        var image = "ghcr.io/raaif-yousuf/dna-entropy-worker@sha256:" + new string('a', 64);
        h.Settings.SetString(JobEngine.WorkerImageSettingsKey, image);

        var jobId = await h.Engine.StartRunAsync(Options(), CancellationToken.None);
        (await h.TerminalAsync()).ShouldBe(JobPhase.Completed);

        var metadata = h.Gcp.CreatedSpecs.Single().Metadata;
        metadata.ShouldNotBeNull();
        metadata!["deg-job-id"].ShouldBe(jobId);
        metadata["deg-worker-image"].ShouldBe(image);
    }

    [Fact]
    public async Task A_preflight_failure_surfaces_as_a_Failed_phase()
    {
        var h = new Harness(new FakeGcp().WithSelectedProject(Project).WithBillingOff(Project));

        await h.Engine.StartRunAsync(Options(), CancellationToken.None);

        (await h.TerminalAsync()).ShouldBe(JobPhase.Failed);
    }

    [Fact]
    public async Task Stopping_the_VM_reaches_the_real_gateway()
    {
        var h = new Harness(new FakeGcp().WithSelectedProject(Project));
        var jobId = await h.Engine.StartRunAsync(Options(after: AfterTaskAction.KeepAlive), CancellationToken.None);
        await h.TerminalAsync();
        (await h.Gcp.FindByJobIdAsync(jobId, CancellationToken.None))[0].Status.ShouldBe("RUNNING");

        await h.Engine.StopVmAsync(jobId, CancellationToken.None);

        (await h.Gcp.FindByJobIdAsync(jobId, CancellationToken.None))[0].Status.ShouldBe("STOPPED");
    }

    [Fact]
    public async Task Deleting_the_VM_reaches_the_real_gateway()
    {
        var h = new Harness(new FakeGcp().WithSelectedProject(Project));
        var jobId = await h.Engine.StartRunAsync(Options(after: AfterTaskAction.KeepAlive), CancellationToken.None);
        await h.TerminalAsync();

        await h.Engine.DeleteVmAsync(jobId, CancellationToken.None);

        (await h.Gcp.FindByJobIdAsync(jobId, CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Cancelling_a_run_blocked_in_CreateVm_ends_Cancelled_with_no_VM()
    {
        var h = new Harness(new FakeGcp().WithSelectedProject(Project).WithBlockedCreate());
        var jobId = await h.Engine.StartRunAsync(Options(), CancellationToken.None);
        await h.Gcp.CreateVmEntered.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);

        await h.Engine.CancelRunAsync(jobId, CancellationToken.None);

        (await h.TerminalAsync()).ShouldBe(JobPhase.Cancelled);
        (await h.Runs.GetAllAsync(CancellationToken.None)).Single(r => r.JobId == jobId).Phase.ShouldBe(JobPhase.Cancelled);
        (await h.Gcp.FindByJobIdAsync(jobId, CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Cancelling_while_a_create_that_ignores_its_token_completes_still_leaves_no_VM()
    {
        // The create was already accepted by the API: it finishes after the
        // caller gave up. The run must not walk on to Preparing, and the
        // cancel must delete the VM that appeared.
        var h = new Harness(new FakeGcp().WithSelectedProject(Project).WithBlockedCreate(ignoreCancellation: true));
        var jobId = await h.Engine.StartRunAsync(Options(), CancellationToken.None);
        await h.Gcp.CreateVmEntered.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);

        var cancel = h.Engine.CancelRunAsync(jobId, CancellationToken.None);
        h.Gcp.ReleaseCreate();
        await cancel.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);

        (await h.TerminalAsync()).ShouldBe(JobPhase.Cancelled);
        h.Messages.Select(m => m.Phase).ShouldNotContain(JobPhase.Preparing);
        (await h.Gcp.FindByJobIdAsync(jobId, CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Cancelling_immediately_after_start_leaves_exactly_one_terminal_state()
    {
        // The window between registering the run and its task starting used
        // to let a cancel see no task and write the row concurrently.
        for (var i = 0; i < 20; i++)
        {
            var h = new Harness(new FakeGcp().WithSelectedProject(Project));
            var jobId = await h.Engine.StartRunAsync(Options(), CancellationToken.None);
            await h.Engine.CancelRunAsync(jobId, CancellationToken.None);

            var phase = (await h.Runs.GetAllAsync(CancellationToken.None)).Single(r => r.JobId == jobId).Phase;
            JobStateMachine.IsTerminal(phase).ShouldBeTrue($"iteration {i} ended in {phase}");
            (await h.Gcp.FindByJobIdAsync(jobId, CancellationToken.None)).Count(v => v.Status == "RUNNING").ShouldBe(0);
        }
    }

    [Fact]
    public async Task A_pre_start_failure_records_a_code_and_no_English_detail()
    {
        var h = new Harness(new FakeGcp().WithSignedOut());

        var jobId = await h.Engine.StartRunAsync(Options(), CancellationToken.None);
        await h.TerminalAsync();

        var row = (await h.Runs.GetAllAsync(CancellationToken.None)).Single(r => r.JobId == jobId);
        row.ErrorCode.ShouldBe(RunErrorCodes.NoProject);
        row.ErrorDetail.ShouldBeNull();
    }

    [Fact]
    public async Task Cancelling_an_unknown_job_id_publishes_nothing_and_does_not_throw()
    {
        var h = new Harness(new FakeGcp().WithSelectedProject(Project));

        await Should.NotThrowAsync(() => h.Engine.CancelRunAsync("no-such-job", CancellationToken.None));

        h.Messages.ShouldBeEmpty();
    }

    [Fact]
    public async Task Cancelling_a_finished_run_changes_nothing()
    {
        var h = new Harness(new FakeGcp().WithSelectedProject(Project));
        var jobId = await h.Engine.StartRunAsync(Options(), CancellationToken.None);
        await h.TerminalAsync();
        var before = h.Messages.Count;

        await h.Engine.CancelRunAsync(jobId, CancellationToken.None);

        h.Messages.Count.ShouldBe(before);
    }
}
