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

/// <summary>Issues #72/#73: the entry that makes the viewer page reachable ("Open viewer" on the run page).</summary>
public sealed class RunProgressOpenViewerTests
{
    private sealed class ImmediateDispatcher : IDispatcher
    {
        public void Enqueue(Action action) => action();
    }

    private readonly IRunRepository _repository = Substitute.For<IRunRepository>();
    private readonly INavigator _navigator = Substitute.For<INavigator>();
    private readonly IMessenger _messenger = new WeakReferenceMessenger();

    private RunProgressViewModel Make(string jobId = "job-1")
    {
        var strings = Substitute.For<IStringResourceProvider>();
        strings.GetString(Arg.Any<string>()).Returns(ci => ci.Arg<string>());
        return new RunProgressViewModel(
            Substitute.For<IJobEngine>(), new ImmediateDispatcher(), Substitute.For<IDialogService>(),
            Substitute.For<IRunVmActions>(), Substitute.For<ILogTailReader>(), strings, _repository, _navigator, _messenger)
        {
            JobId = jobId,
        };
    }

    private void RunHasOutputDir(string? dir) =>
        _repository.GetAllAsync(Arg.Any<CancellationToken>()).Returns(
            new[] { new RunRecord("other", JobPhase.Completed, DateTimeOffset.UtcNow, OutputDir: @"C:\wrong"), new RunRecord("job-1", JobPhase.Completed, DateTimeOffset.UtcNow, OutputDir: dir) });

    [Theory]
    [InlineData(JobPhase.Completed)]
    [InlineData(JobPhase.PartiallyCompleted)]
    public void Open_viewer_is_enabled_on_a_finished_run_with_an_output_folder_and_navigates_to_the_viewer_page(JobPhase phase)
    {
        RunHasOutputDir(@"C:\out\sample");
        var vm = Make();

        _messenger.Send(new RunPhaseChangedMessage("job-1", phase));

        vm.OpenViewerCommand.CanExecute(null).ShouldBeTrue();
        vm.OpenViewerCommand.Execute(null);
        _navigator.Received(1).NavigateTo(ViewerViewModel.PageKey, @"C:\out\sample");
    }

    [Theory]
    [InlineData(JobPhase.Draft)]
    [InlineData(JobPhase.Running)]
    [InlineData(JobPhase.Downloading)]
    [InlineData(JobPhase.Failed)]
    [InlineData(JobPhase.Cancelled)]
    public void Open_viewer_is_disabled_on_every_other_phase_even_with_an_output_folder(JobPhase phase)
    {
        RunHasOutputDir(@"C:\out\sample");
        var vm = Make();
        _messenger.Send(new RunPhaseChangedMessage("job-1", phase));
        vm.OpenViewerCommand.CanExecute(null).ShouldBeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Open_viewer_is_disabled_when_the_output_folder_is_not_known(string? dir)
    {
        RunHasOutputDir(dir);
        var vm = Make();
        _messenger.Send(new RunPhaseChangedMessage("job-1", JobPhase.Completed));
        vm.OpenViewerCommand.CanExecute(null).ShouldBeFalse();
    }

    [Fact]
    public void A_repository_failure_leaves_open_viewer_disabled_instead_of_throwing()
    {
        _repository.GetAllAsync(Arg.Any<CancellationToken>()).Returns<IReadOnlyList<RunRecord>>(_ => throw new InvalidOperationException("db"));
        var vm = Make();
        Should.NotThrow(() => _messenger.Send(new RunPhaseChangedMessage("job-1", JobPhase.Completed)));
        vm.OpenViewerCommand.CanExecute(null).ShouldBeFalse();
    }

    [Fact]
    public void Open_viewer_is_disabled_again_when_the_page_is_reused_for_another_job()
    {
        RunHasOutputDir(@"C:\out\sample");
        var vm = Make();
        _messenger.Send(new RunPhaseChangedMessage("job-1", JobPhase.Completed));
        vm.OpenViewerCommand.CanExecute(null).ShouldBeTrue();

        vm.JobId = "job-2";

        vm.OpenViewerCommand.CanExecute(null).ShouldBeFalse();
    }
}
