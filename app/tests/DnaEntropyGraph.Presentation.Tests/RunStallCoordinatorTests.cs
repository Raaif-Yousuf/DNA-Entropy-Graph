using CommunityToolkit.Mvvm.Messaging;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Presentation.Messaging;
using DnaEntropyGraph.Presentation.Services;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

/// <summary>
/// Issue #675, the app side: the runner's slow-run notice reaches the run page as a message, and its stall question is a Stop / Delete / Keep waiting dialog
/// whose button becomes the <see cref="StallChoice"/> the runner acts on. A dialog that cannot be shown must never end the run.
/// </summary>
public class RunStallCoordinatorTests
{
    private readonly IChoiceDialogService _dialog = Substitute.For<IChoiceDialogService>();
    private readonly IDiagnosticsLog _log = Substitute.For<IDiagnosticsLog>();
    private readonly IStringResourceProvider _strings = Substitute.For<IStringResourceProvider>();
    private readonly WeakReferenceMessenger _messenger = new();
    private readonly List<RunNoticeMessage> _notices = [];

    public RunStallCoordinatorTests()
    {
        // {0} stage, {1} minutes, {2} computer, {3} run: every template here prints all four so a test sees what each one was given.
        _strings.GetString(Arg.Any<string>()).Returns(call => call.Arg<string>() + " {0}|{1}|{2}|{3}");
        _strings.GetString("RunNotice_Slow").Returns("RunNotice_Slow {0}|{1}");
        _strings.GetString("RunNotice_Stalled").Returns("RunNotice_Stalled {0}|{1}");
        _strings.GetString("StallDialog_RunLabel").Returns("StallDialog_RunLabel {0}|{1}");
        _strings.GetString("StallDialog_RunLabelNoName").Returns("StallDialog_RunLabelNoName {0}");
        _messenger.Register<RunNoticeMessage>(this, (_, m) => _notices.Add(m));
    }

    private RunStallCoordinator NewCoordinator() => new(_dialog, _messenger, _strings, _log);

    private static StallPrompt Prompt(string code = RunErrorCodes.WorkerStageStuck) => new("job-1", code, "stuck", "predict", TimeSpan.FromMinutes(12), "a.gb", "deg-job-1");

    [Theory]
    [InlineData(0, StallChoice.Stop)]
    [InlineData(2, StallChoice.KeepWaiting)]
    public async Task Each_button_becomes_the_choice_the_runner_acts_on(int pressed, StallChoice expected)
    {
        _dialog.ChooseAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns(pressed);

        var choice = await NewCoordinator().AskAsync(Prompt(), CancellationToken.None);

        choice.ShouldBe(expected);
    }

    [Fact]
    public async Task Keep_waiting_clears_the_notice_so_the_window_is_asking_line_does_not_stay_until_the_run_ends()
    {
        // #740 finding 1: the runner sends no StallCleared after Keep waiting, so the coordinator that showed the notice must clear it.
        _dialog.ChooseAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns(2);

        await NewCoordinator().AskAsync(Prompt(), CancellationToken.None);

        _notices.Count.ShouldBe(1);
        _notices[0].JobId.ShouldBe("job-1");
        _notices[0].Text.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_newer_slow_notice_that_arrived_while_the_question_was_open_is_not_wiped_when_it_ends()
    {
        // #740 round 2 (L4): the question's end clears only the stall line it raised, never a newer notice for the same run.
        var answer = new TaskCompletionSource<int?>();
        _dialog.ChooseAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns(_ => answer.Task);
        var coordinator = NewCoordinator();
        var asking = coordinator.AskAsync(Prompt(), CancellationToken.None);
        coordinator.OnNotice(new RunNotice("job-1", RunNoticeKind.StallDetected, "predict", Code: RunErrorCodes.WorkerStageStuck));
        coordinator.OnNotice(new RunNotice("job-1", RunNoticeKind.StallCleared));
        coordinator.OnNotice(new RunNotice("job-1", RunNoticeKind.SlowStage, "export", TimeSpan.FromMinutes(9), TimeSpan.FromMinutes(12)));

        answer.SetResult(2);
        await asking;

        _notices.Last().Text.ShouldContain("RunNotice_Slow", Case.Sensitive, "the slow notice raised after the stall must still be the one on the page");
    }

    [Fact]
    public async Task A_question_that_ended_before_the_runner_announced_it_never_leaves_its_stall_line_behind()
    {
        // The dialog can answer (no window: Keep waiting) before the runner's StallDetected notice is published.
        _dialog.ChooseAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns((int?)null);
        var coordinator = NewCoordinator();

        await coordinator.AskAsync(Prompt(), CancellationToken.None);
        coordinator.OnNotice(new RunNotice("job-1", RunNoticeKind.StallDetected, "predict", Code: RunErrorCodes.WorkerStageStuck));

        _notices.Last().Text.ShouldBeEmpty();
        _notices.ShouldNotContain(n => n.Text.Contains("RunNotice_Stalled"));
    }

    [Fact]
    public async Task A_question_the_runner_withdrew_also_clears_the_notice()
    {
        _dialog.ChooseAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns<Task<int?>>(_ => throw new OperationCanceledException());

        await NewCoordinator().AskAsync(Prompt(), CancellationToken.None);

        _notices.Select(n => n.Text).ShouldBe([string.Empty]);
    }

    [Fact]
    public async Task The_question_names_the_run_and_the_computer_so_two_queued_questions_differ()
    {
        string? title = null;
        string? body = null;
        _dialog.ChooseAsync(Arg.Do<string>(m => title = m), Arg.Do<string>(m => body = m), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns(2);

        await NewCoordinator().AskAsync(Prompt(), CancellationToken.None);
        var first = (title, body);
        await NewCoordinator().AskAsync(new StallPrompt("job-2", RunErrorCodes.WorkerStageStuck, "stuck", "predict", TimeSpan.FromMinutes(12), "b.fasta", "deg-job-2"), CancellationToken.None);

        first.title.ShouldNotBeNull().ShouldContain("StallDialog_RunLabel a.gb|job-1");
        first.body.ShouldNotBeNull().ShouldContain("deg-job-1");
        title.ShouldNotBeNull().ShouldContain("b.fasta|job-2");
        body.ShouldNotBeNull().ShouldContain("deg-job-2");
        title.ShouldNotBe(first.title);
    }

    [Fact]
    public async Task A_run_with_no_known_name_is_still_named_by_its_job_and_a_missing_computer_name_says_this_computer()
    {
        string? title = null;
        _dialog.ChooseAsync(Arg.Do<string>(m => title = m), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns(2);

        await NewCoordinator().AskAsync(new StallPrompt("job-7", RunErrorCodes.WorkerStageStuck, "stuck", "predict", TimeSpan.FromMinutes(3)), CancellationToken.None);

        title.ShouldNotBeNull().ShouldContain("job-7");
    }

    [Fact]
    public async Task Delete_is_confirmed_by_a_second_question_naming_the_computer_and_only_its_delete_button_deletes()
    {
        var asked = new List<(string Title, string Body, IReadOnlyList<string> Labels)>();
        _dialog.ChooseAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                asked.Add((call.ArgAt<string>(0), call.ArgAt<string>(1), call.ArgAt<IReadOnlyList<string>>(2)));
                return Task.FromResult<int?>(asked.Count == 1 ? 1 : 0);
            });

        var choice = await NewCoordinator().AskAsync(Prompt(), CancellationToken.None);

        choice.ShouldBe(StallChoice.Delete);
        asked.Count.ShouldBe(2);
        asked[1].Title.ShouldContain("StallConfirm_Title");
        asked[1].Title.ShouldContain("deg-job-1");
        asked[1].Body.ShouldContain("deg-job-1");
        asked[1].Labels.Count.ShouldBe(2);
        asked[1].Labels[0].ShouldContain("StallConfirm_Delete");
        asked[1].Labels[1].ShouldContain("StallDialog_KeepWaiting");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(null)]
    public async Task Backing_out_of_the_delete_confirmation_deletes_nothing_and_keeps_waiting(int? secondAnswer)
    {
        var count = 0;
        _dialog.ChooseAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<int?>(++count == 1 ? 1 : secondAnswer));

        var choice = await NewCoordinator().AskAsync(Prompt(), CancellationToken.None);

        choice.ShouldBe(StallChoice.KeepWaiting);
        count.ShouldBe(2);
    }

    [Fact]
    public async Task The_dialog_offers_exactly_stop_delete_and_keep_waiting_in_that_order_and_says_why()
    {
        IReadOnlyList<string>? labels = null;
        string? body = null;
        _dialog.ChooseAsync(Arg.Any<string>(), Arg.Do<string>(m => body = m), Arg.Do<IReadOnlyList<string>>(l => labels = l), Arg.Any<CancellationToken>()).Returns(2);

        await NewCoordinator().AskAsync(Prompt(RunErrorCodes.WorkerHeartbeatStale), CancellationToken.None);

        labels.ShouldBe(["StallDialog_Stop {0}|{1}|{2}|{3}", "StallDialog_Delete {0}|{1}|{2}|{3}", "StallDialog_KeepWaiting {0}|{1}|{2}|{3}"]);
        body.ShouldNotBeNull().ShouldContain("StallDialog_Body_Stale");
    }

    [Theory]
    [InlineData(RunErrorCodes.WorkerStageStuck, "StallDialog_Body_Stuck")]
    [InlineData(RunErrorCodes.WorkerHeartbeatStale, "StallDialog_Body_Stale")]
    [InlineData(RunErrorCodes.WorkerNoHeartbeat, "StallDialog_Body_NoHeartbeat")]
    [InlineData("something_else", "StallDialog_Body_Stuck")]
    public async Task The_message_matches_the_kind_of_stall(string code, string key)
    {
        string? body = null;
        _dialog.ChooseAsync(Arg.Any<string>(), Arg.Do<string>(m => body = m), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns(2);

        await NewCoordinator().AskAsync(Prompt(code), CancellationToken.None);

        body.ShouldNotBeNull().ShouldContain(key);
    }

    [Fact]
    public async Task A_dialog_that_could_not_be_shown_or_was_closed_by_the_runner_is_keep_waiting_never_a_deletion()
    {
        _dialog.ChooseAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns((int?)null);
        (await NewCoordinator().AskAsync(Prompt(), CancellationToken.None)).ShouldBe(StallChoice.KeepWaiting);

        _dialog.ChooseAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns<Task<int?>>(_ => throw new OperationCanceledException());
        (await NewCoordinator().AskAsync(Prompt(), CancellationToken.None)).ShouldBe(StallChoice.KeepWaiting);
    }

    [Fact]
    public void A_slow_stage_notice_reaches_the_run_page_with_the_stage_and_names_an_action_and_is_logged_by_kind()
    {
        NewCoordinator().OnNotice(new RunNotice("job-1", RunNoticeKind.SlowStage, "predict", TimeSpan.FromMinutes(9), TimeSpan.FromMinutes(12)));

        _notices.Count.ShouldBe(1);
        _notices[0].JobId.ShouldBe("job-1");
        _notices[0].Text.ShouldContain("RunNotice_Slow");
        _notices[0].Text.ShouldContain("predict");
        _log.Received(1).Warning("run-notice", "job-1", "SlowStage");
    }

    [Fact]
    public void A_detected_stall_says_a_question_is_open_and_a_cleared_stall_empties_the_notice()
    {
        var coordinator = NewCoordinator();

        coordinator.OnNotice(new RunNotice("job-1", RunNoticeKind.StallDetected, "predict", Code: RunErrorCodes.WorkerStageStuck));
        coordinator.OnNotice(new RunNotice("job-1", RunNoticeKind.StallCleared));

        _notices.Count.ShouldBe(2);
        _notices[0].Text.ShouldContain("RunNotice_Stalled");
        _notices[1].Text.ShouldBeEmpty();
    }

    [Fact]
    public void Waiting_for_a_graphics_card_is_shown_as_a_line_and_withdrawn_when_it_ends()
    {
        // Issue #92.
        _strings.GetString("RunNotice_WaitingForGpu").Returns("waiting for a card");
        var coordinator = NewCoordinator();

        coordinator.OnNotice(new RunNotice("job-1", RunNoticeKind.WaitingForGpu));
        coordinator.OnNotice(new RunNotice("job-1", RunNoticeKind.WaitingForGpuEnded));

        _notices.Select(n => n.Text).ShouldBe(["waiting for a card", string.Empty]);
        _notices.ShouldAllBe(n => n.JobId == "job-1");
    }
}
