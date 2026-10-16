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

/// <summary>Issue #675: the run page shows the slow-run notice for its own run only, and clears it when the runner says so.</summary>
public class RunProgressNoticeTests
{
    private sealed class ImmediateDispatcher : IDispatcher
    {
        public void Enqueue(Action action) => action();
    }

    private static (RunProgressViewModel ViewModel, WeakReferenceMessenger Messenger) Create()
    {
        var strings = Substitute.For<IStringResourceProvider>();
        strings.GetString(Arg.Any<string>()).Returns(call => call.Arg<string>());
        var messenger = new WeakReferenceMessenger();
        var viewModel = new RunProgressViewModel(
            Substitute.For<IJobEngine>(), new ImmediateDispatcher(), Substitute.For<IDialogService>(), Substitute.For<IRunVmActions>(),
            Substitute.For<ILogTailReader>(), strings, Substitute.For<IRunRepository>(), Substitute.For<INavigator>(), messenger)
        {
            JobId = "job-1",
        };
        return (viewModel, messenger);
    }

    [Fact]
    public void A_notice_for_this_run_shows_and_an_empty_one_clears_it()
    {
        var (viewModel, messenger) = Create();
        viewModel.HasSlowNotice.ShouldBeFalse();

        messenger.Send(new RunNoticeMessage("job-1", "taking longer"));
        viewModel.SlowNoticeText.ShouldBe("taking longer");
        viewModel.HasSlowNotice.ShouldBeTrue();

        messenger.Send(new RunNoticeMessage("job-1", string.Empty));
        viewModel.HasSlowNotice.ShouldBeFalse();
    }

    [Fact]
    public void A_notice_for_another_run_is_ignored()
    {
        var (viewModel, messenger) = Create();

        messenger.Send(new RunNoticeMessage("job-2", "not mine"));

        viewModel.HasSlowNotice.ShouldBeFalse();
    }
}
