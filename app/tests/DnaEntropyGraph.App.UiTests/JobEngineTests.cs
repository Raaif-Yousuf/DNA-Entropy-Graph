using CommunityToolkit.Mvvm.Messaging;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Presentation.Messaging;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.App.UiTests;

/// <summary>
/// JobEngine is a plain class with no WinUI window or dispatcher of its
/// own (see its own doc comment), so unlike <see cref="SmokeTests"/> this
/// does not need an interactive session - it needs no XamlRoot and creates
/// no Window. This is the observable that crosses the seam ShellViewModel's
/// tests stop at: a run started through the real, production
/// <see cref="JobEngine"/> actually reaches the messenger ShellViewModel
/// subscribes to, not just a hand-rolled message sent directly in a
/// ShellViewModel-only test.
/// </summary>
public class JobEngineTests
{
    [Fact]
    public async Task Starting_a_run_publishes_a_phase_changed_message_the_shell_can_react_to()
    {
        var messenger = new WeakReferenceMessenger();
        var jobEngine = new JobEngine(messenger);
        RunPhaseChangedMessage? received = null;
        messenger.Register<JobEngineTests, RunPhaseChangedMessage>(this, (_, message) => received = message);

        var jobId = await jobEngine.StartRunAsync(new RunOptions { ModelId = "evo2_7b", RunTarget = "Cloud" }, CancellationToken.None);

        received.ShouldNotBeNull();
        received!.JobId.ShouldBe(jobId);
        received.Phase.ShouldBe(JobPhase.Validating);
    }

    [Fact]
    public async Task Cancelling_a_run_publishes_a_terminal_phase()
    {
        var messenger = new WeakReferenceMessenger();
        var jobEngine = new JobEngine(messenger);
        var jobId = await jobEngine.StartRunAsync(new RunOptions { ModelId = "evo2_7b", RunTarget = "Cloud" }, CancellationToken.None);

        RunPhaseChangedMessage? received = null;
        messenger.Register<JobEngineTests, RunPhaseChangedMessage>(this, (_, message) => received = message);
        await jobEngine.CancelRunAsync(jobId, CancellationToken.None);

        received.ShouldNotBeNull();
        received!.JobId.ShouldBe(jobId);
        received.Phase.ShouldBe(JobPhase.Cancelled);
    }

    [Fact]
    public async Task Stopping_the_VM_for_an_active_job_publishes_a_terminal_phase()
    {
        // Documented placeholder (see JobEngine.StopVmAsync's own doc
        // comment): no CloudJobRunner exists yet to hold a real VM, so
        // "stop" degrades to the same effect as cancelling. This test
        // pins that degraded behaviour down so a future real
        // implementation change is a deliberate edit, not an accident.
        var messenger = new WeakReferenceMessenger();
        var jobEngine = new JobEngine(messenger);
        var jobId = await jobEngine.StartRunAsync(new RunOptions { ModelId = "evo2_7b", RunTarget = "Cloud" }, CancellationToken.None);
        RunPhaseChangedMessage? received = null;
        messenger.Register<JobEngineTests, RunPhaseChangedMessage>(this, (_, message) => received = message);

        await jobEngine.StopVmAsync(jobId, CancellationToken.None);

        received.ShouldNotBeNull();
        received!.Phase.ShouldBe(JobPhase.Cancelled);
    }

    [Fact]
    public async Task Deleting_the_VM_for_an_active_job_publishes_a_terminal_phase()
    {
        var messenger = new WeakReferenceMessenger();
        var jobEngine = new JobEngine(messenger);
        var jobId = await jobEngine.StartRunAsync(new RunOptions { ModelId = "evo2_7b", RunTarget = "Cloud" }, CancellationToken.None);
        RunPhaseChangedMessage? received = null;
        messenger.Register<JobEngineTests, RunPhaseChangedMessage>(this, (_, message) => received = message);

        await jobEngine.DeleteVmAsync(jobId, CancellationToken.None);

        received.ShouldNotBeNull();
        received!.Phase.ShouldBe(JobPhase.Cancelled);
    }

    [Fact]
    public async Task Cancelling_an_unknown_job_id_publishes_nothing()
    {
        // Neighbour case: JobEngine.CancelRunAsync is a documented no-op for
        // a job id it never started (see its own dictionary-removal logic) -
        // it must not announce a phase change for a run that was never real.
        var messenger = new WeakReferenceMessenger();
        var jobEngine = new JobEngine(messenger);
        var receivedCount = 0;
        messenger.Register<JobEngineTests, RunPhaseChangedMessage>(this, (_, _) => receivedCount++);

        await jobEngine.CancelRunAsync("no-such-job", CancellationToken.None);

        receivedCount.ShouldBe(0);
    }
}
