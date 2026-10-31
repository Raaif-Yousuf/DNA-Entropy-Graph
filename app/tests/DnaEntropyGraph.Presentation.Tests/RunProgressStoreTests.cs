using CommunityToolkit.Mvvm.Messaging;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Contract;
using DnaEntropyGraph.Presentation.Messaging;
using DnaEntropyGraph.Presentation.Services;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

/// <summary>
/// Issue #1086 review: progress is published on change only and the run page is a transient ViewModel, so a page created (or shown again) mid-run
/// missed every earlier event and stayed blank until the worker changed again. The store remembers the last progress per run.
/// </summary>
public class RunProgressStoreTests
{
    [Fact]
    public void It_holds_the_latest_progress_for_a_run_and_nothing_for_another_run()
    {
        var messenger = new WeakReferenceMessenger();
        var store = new RunProgressStore(messenger);

        messenger.Send(new RunProgressChangedMessage("job-1", new ProgressEvent("running", 0.2, 2, 10)));
        messenger.Send(new RunProgressChangedMessage("job-1", new ProgressEvent("running", 0.4, 4, 10)));

        store.Latest("job-1").ShouldBe(new ProgressEvent("running", 0.4, 4, 10));
        store.Latest("job-2").ShouldBeNull();
    }

    [Theory]
    [InlineData(JobPhase.Completed)]
    [InlineData(JobPhase.Failed)]
    [InlineData(JobPhase.Cancelled)]
    [InlineData(JobPhase.PartiallyCompleted)]
    public void A_finished_run_is_forgotten(JobPhase terminal)
    {
        var messenger = new WeakReferenceMessenger();
        var store = new RunProgressStore(messenger);
        messenger.Send(new RunProgressChangedMessage("job-1", new ProgressEvent("running", 0.4, 4, 10)));

        messenger.Send(new RunPhaseChangedMessage("job-1", terminal));

        store.Latest("job-1").ShouldBeNull();
    }

    [Fact]
    public void A_non_terminal_phase_keeps_the_progress()
    {
        var messenger = new WeakReferenceMessenger();
        var store = new RunProgressStore(messenger);
        messenger.Send(new RunProgressChangedMessage("job-1", new ProgressEvent("running", 0.4, 4, 10)));

        messenger.Send(new RunPhaseChangedMessage("job-1", JobPhase.Running));

        store.Latest("job-1").ShouldNotBeNull();
    }
}
