using CommunityToolkit.Mvvm.Messaging;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Presentation.Messaging;
using DnaEntropyGraph.Presentation.Services;
using DnaEntropyGraph.Presentation.ViewModels;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

/// <summary>Issue #102: a finished run on the run page leads to the Results page for that run.</summary>
public sealed class RunProgressOpenResultsTests
{
    private sealed class ImmediateDispatcher : IDispatcher
    {
        public void Enqueue(Action action) => action();
    }

    private readonly INavigator _navigator = Substitute.For<INavigator>();
    private readonly IMessenger _messenger = new WeakReferenceMessenger();

    private RunProgressViewModel Make()
    {
        var strings = Substitute.For<IStringResourceProvider>();
        strings.GetString(Arg.Any<string>()).Returns(ci => ci.Arg<string>());
        return new RunProgressViewModel(
            Substitute.For<IJobEngine>(), new ImmediateDispatcher(), Substitute.For<IDialogService>(),
            Substitute.For<IRunVmActions>(), Substitute.For<ILogTailReader>(), strings, Substitute.For<IRunRepository>(), _navigator, _messenger)
        {
            JobId = "job-1",
        };
    }

    [Theory]
    [InlineData(JobPhase.Completed)]
    [InlineData(JobPhase.PartiallyCompleted)]
    public void A_finished_run_can_open_its_results_page_by_job_id(JobPhase phase)
    {
        var vm = Make();

        _messenger.Send(new RunPhaseChangedMessage("job-1", phase));

        vm.OpenResultsCommand.CanExecute(null).ShouldBeTrue();
        vm.OpenResultsCommand.Execute(null);
        _navigator.Received(1).NavigateTo(ResultsViewModel.PageKey, "job-1");
    }

    [Theory]
    [InlineData(JobPhase.Draft)]
    [InlineData(JobPhase.Running)]
    [InlineData(JobPhase.Downloading)]
    [InlineData(JobPhase.Failed)]
    [InlineData(JobPhase.Cancelled)]
    public void Every_other_phase_has_no_results_to_open(JobPhase phase)
    {
        var vm = Make();

        _messenger.Send(new RunPhaseChangedMessage("job-1", phase));

        vm.OpenResultsCommand.CanExecute(null).ShouldBeFalse();
    }
}
