using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Presentation.Services;
using DnaEntropyGraph.Presentation.ViewModels;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

/// <summary>Issue #107: what the app does when a Windows notification is clicked, or a second launch hands its arguments over.</summary>
public class ActivationRouterTests
{
    private readonly INavigator _navigator = Substitute.For<INavigator>();
    private readonly IAppWindow _window = Substitute.For<IAppWindow>();
    private readonly ActivationRouter _router;

    public ActivationRouterTests() => _router = new ActivationRouter(_navigator, _window, new InlineDispatcher());

    [Fact]
    public void A_run_link_brings_the_window_forward_and_opens_that_runs_results()
    {
        _router.Handle(AppDeepLink.ForRun("job-7")).ShouldBeTrue();

        _window.Received(1).BringToFront();
        _navigator.Received(1).NavigateTo(ResultsViewModel.PageKey, "job-7");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("run=../x")]
    public void A_plain_launch_or_a_bad_link_only_brings_the_window_forward(string? arguments)
    {
        _router.Handle(arguments).ShouldBeFalse();

        _window.Received(1).BringToFront();
        _navigator.DidNotReceiveWithAnyArgs().NavigateTo(default!, default);
    }

    [Fact]
    public void A_launch_with_a_sequence_file_opens_New_run_with_that_file_added()
    {
        _router.Handle("\"C:\\data\\my seq.gb\"").ShouldBeFalse("it named a file, not a run");

        _window.Received(1).BringToFront();
        _navigator.Received(1).NavigateTo(
            ShellViewModel.InitialPageKeyValue,
            Arg.Is<DroppedItems>(d => d.Paths.SequenceEqual(new[] { @"C:\data\my seq.gb" }) && !d.Failed && d.SkippedVirtual == 0));
    }

    [Fact]
    public void A_run_link_wins_over_a_file_in_the_same_launch()
    {
        _router.Handle("run=job-3;\"C:\\data\\a.gb\"").ShouldBeTrue();

        _navigator.Received(1).NavigateTo(ResultsViewModel.PageKey, "job-3");
        _navigator.DidNotReceive().NavigateTo(ShellViewModel.InitialPageKeyValue, Arg.Any<object?>());
    }

    private sealed class InlineDispatcher : IDispatcher
    {
        public void Enqueue(Action action) => action();
    }
}
