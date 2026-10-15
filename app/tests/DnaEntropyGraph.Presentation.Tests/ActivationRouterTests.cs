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

    [Fact]
    public void An_activation_from_another_thread_touches_the_window_and_navigator_only_on_the_dispatcher_thread()
    {
        // Notification clicks and redirected launches arrive on a thread-pool thread; the window and the frame are UI-thread objects.
        var queue = new QueueDispatcher();
        var router = new ActivationRouter(_navigator, _window, queue);
        var callerThread = -1;
        var touchedOn = new List<int>();
        _window.When(w => w.BringToFront()).Do(_ => touchedOn.Add(Environment.CurrentManagedThreadId));
        _navigator.When(n => n.NavigateTo(Arg.Any<string>(), Arg.Any<object?>())).Do(_ => touchedOn.Add(Environment.CurrentManagedThreadId));

        var caller = new Thread(() =>
        {
            callerThread = Environment.CurrentManagedThreadId;
            router.Handle(AppDeepLink.ForRun("job-7"));
        });
        caller.Start();
        caller.Join();

        touchedOn.ShouldBeEmpty("nothing runs on the calling thread");
        queue.RunPending();

        touchedOn.Count.ShouldBe(2);
        touchedOn.ShouldAllBe(t => t == Environment.CurrentManagedThreadId && t != callerThread);
    }

    [Fact]
    public void A_launch_before_the_shell_is_ready_waits_so_the_first_navigation_cannot_overwrite_it()
    {
        var readiness = new ShellReadiness();
        var router = new ActivationRouter(_navigator, _window, new InlineDispatcher(), readiness);

        router.Handle("\"C:\\data\\a.gb\"");

        _window.DidNotReceive().BringToFront();
        _navigator.DidNotReceiveWithAnyArgs().NavigateTo(default!, default);

        readiness.MarkReady();

        _window.Received(1).BringToFront();
        _navigator.Received(1).NavigateTo(ShellViewModel.InitialPageKeyValue, Arg.Any<object?>());
    }

    [Fact]
    public void A_run_link_before_the_shell_is_ready_waits_too_and_still_reports_that_it_named_a_run()
    {
        var readiness = new ShellReadiness();
        var router = new ActivationRouter(_navigator, _window, new InlineDispatcher(), readiness);

        router.Handle(AppDeepLink.ForRun("job-7")).ShouldBeTrue();
        _navigator.DidNotReceiveWithAnyArgs().NavigateTo(default!, default);

        readiness.MarkReady();

        _navigator.Received(1).NavigateTo(ResultsViewModel.PageKey, "job-7");
    }

    [Fact]
    public void Files_go_into_the_New_run_screen_that_is_showing_instead_of_a_fresh_page_that_would_drop_its_draft()
    {
        var intake = new NewRunIntake();
        var screen = new RecordingScreen();
        intake.Attach(screen);
        var router = new ActivationRouter(_navigator, _window, new InlineDispatcher(), intake: intake);

        router.Handle("\"C:\\data\\a.gb\"");

        screen.Received.ShouldHaveSingleItem().Paths.ShouldBe([@"C:\data\a.gb"]);
        _window.Received(1).BringToFront();
        _navigator.DidNotReceiveWithAnyArgs().NavigateTo(default!, default);
    }

    [Fact]
    public void With_no_New_run_screen_showing_the_files_open_one_through_navigation()
    {
        var router = new ActivationRouter(_navigator, _window, new InlineDispatcher(), intake: new NewRunIntake());

        router.Handle("\"C:\\data\\a.gb\"");

        _navigator.Received(1).NavigateTo(ShellViewModel.InitialPageKeyValue, Arg.Any<DroppedItems>());
    }

    private sealed class RecordingScreen : INewRunScreen
    {
        public List<DroppedItems> Received { get; } = [];

        public void Add(DroppedItems items) => Received.Add(items);
    }

    private sealed class QueueDispatcher : IDispatcher
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _work = new();

        public void Enqueue(Action action) => _work.Enqueue(action);

        public void RunPending()
        {
            while (_work.TryDequeue(out var action))
            {
                action();
            }
        }
    }

    private sealed class InlineDispatcher : IDispatcher
    {
        public void Enqueue(Action action) => action();
    }
}
