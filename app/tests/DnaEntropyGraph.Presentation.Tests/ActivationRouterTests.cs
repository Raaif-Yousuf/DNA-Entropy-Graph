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

    private sealed class InlineDispatcher : IDispatcher
    {
        public void Enqueue(Action action) => action();
    }
}
