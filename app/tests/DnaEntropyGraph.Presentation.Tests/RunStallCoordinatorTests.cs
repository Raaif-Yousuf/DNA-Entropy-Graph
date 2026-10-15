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
        _strings.GetString(Arg.Any<string>()).Returns(call => call.Arg<string>() + " {0} {1}");
        _messenger.Register<RunNoticeMessage>(this, (_, m) => _notices.Add(m));
    }

    private RunStallCoordinator NewCoordinator() => new(_dialog, _messenger, _strings, _log);

    private static StallPrompt Prompt(string code = RunErrorCodes.WorkerStageStuck) => new("job-1", code, "stuck", "predict", TimeSpan.FromMinutes(12));

    [Theory]
    [InlineData(0, StallChoice.Stop)]
    [InlineData(1, StallChoice.Delete)]
    [InlineData(2, StallChoice.KeepWaiting)]
    public async Task Each_button_becomes_the_choice_the_runner_acts_on(int pressed, StallChoice expected)
    {
        _dialog.ChooseAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns(pressed);

        var choice = await NewCoordinator().AskAsync(Prompt(), CancellationToken.None);

        choice.ShouldBe(expected);
    }

    [Fact]
    public async Task The_dialog_offers_exactly_stop_delete_and_keep_waiting_in_that_order_and_says_why()
    {
        IReadOnlyList<string>? labels = null;
        string? body = null;
        _dialog.ChooseAsync(Arg.Any<string>(), Arg.Do<string>(m => body = m), Arg.Do<IReadOnlyList<string>>(l => labels = l), Arg.Any<CancellationToken>()).Returns(2);

        await NewCoordinator().AskAsync(Prompt(RunErrorCodes.WorkerHeartbeatStale), CancellationToken.None);

        labels.ShouldBe(["StallDialog_Stop {0} {1}", "StallDialog_Delete {0} {1}", "StallDialog_KeepWaiting {0} {1}"]);
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
}
