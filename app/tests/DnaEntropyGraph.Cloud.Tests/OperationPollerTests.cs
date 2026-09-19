using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>
/// Issue #256. No test here awaits real time - <see cref="OperationPoller.PollAsync{T}"/>
/// takes an injectable delay function, and every test supplies one that
/// completes instantly while recording what it was asked to wait, so the
/// full backoff/deadline algorithm runs in milliseconds.
/// </summary>
public class OperationPollerTests
{
    private static Func<TimeSpan, CancellationToken, Task> RecordingInstantDelay(List<TimeSpan> requested)
        => (span, _) =>
        {
            requested.Add(span);
            return Task.CompletedTask;
        };

    [Fact]
    public async Task An_operation_that_is_already_done_on_the_first_poll_never_waits()
    {
        var delays = new List<TimeSpan>();

        var outcome = await OperationPoller.PollAsync<string>(
            _ => Task.FromResult(new OperationPoll<string>(true, "ok", null)),
            deadline: TimeSpan.FromSeconds(30),
            CancellationToken.None,
            RecordingInstantDelay(delays));

        outcome.Success.ShouldBeTrue();
        outcome.Value.ShouldBe("ok");
        delays.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_real_error_on_the_first_poll_surfaces_immediately_not_as_a_timeout()
    {
        // The issue's own observable: "An insert that fails with
        // ZONE_RESOURCE_POOL_EXHAUSTED surfaces that code from the polled
        // operation, not a generic timeout."
        var delays = new List<TimeSpan>();

        var outcome = await OperationPoller.PollAsync<string>(
            _ => Task.FromResult(new OperationPoll<string>(true, null, new CloudError("ZONE_RESOURCE_POOL_EXHAUSTED", null, "no capacity"))),
            deadline: TimeSpan.FromSeconds(30),
            CancellationToken.None,
            RecordingInstantDelay(delays));

        outcome.Success.ShouldBeFalse();
        outcome.Error!.Code.ShouldBe("ZONE_RESOURCE_POOL_EXHAUSTED");
        delays.ShouldBeEmpty();
    }

    [Fact]
    public async Task Backoff_starts_at_one_second_and_doubles_up_to_a_ten_second_cap()
    {
        var delays = new List<TimeSpan>();
        var pollCount = 0;

        var outcome = await OperationPoller.PollAsync<string>(
            _ =>
            {
                pollCount++;
                // Pending for the first five polls, done on the sixth -
                // enough to see the cap actually engage (1, 2, 4, 8, 10).
                return Task.FromResult(pollCount <= 5
                    ? new OperationPoll<string>(false, null, null)
                    : new OperationPoll<string>(true, "ok", null));
            },
            deadline: TimeSpan.FromMinutes(5),
            CancellationToken.None,
            RecordingInstantDelay(delays));

        outcome.Success.ShouldBeTrue();
        delays.ShouldBe([
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(4),
            TimeSpan.FromSeconds(8),
            TimeSpan.FromSeconds(10),
        ]);
    }

    [Fact]
    public async Task An_operation_that_never_reports_done_times_out_at_the_deadline_not_before_or_after()
    {
        var delays = new List<TimeSpan>();

        var outcome = await OperationPoller.PollAsync<string>(
            _ => Task.FromResult(new OperationPoll<string>(false, null, null)),
            deadline: TimeSpan.FromSeconds(10),
            CancellationToken.None,
            RecordingInstantDelay(delays));

        outcome.Success.ShouldBeFalse();
        outcome.Error!.Code.ShouldBe("OPERATION_POLL_TIMEOUT");
        // 1s, 2s, 4s, then the next backoff (8s) clipped to the remaining
        // 3s so the total never overshoots the deadline.
        delays.ShouldBe([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(3)]);
        delays.Aggregate(TimeSpan.Zero, (a, b) => a + b).ShouldBe(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task A_timeout_error_is_never_confused_with_a_real_operation_error_of_the_same_shape()
    {
        var timedOut = await OperationPoller.PollAsync<string>(
            _ => Task.FromResult(new OperationPoll<string>(false, null, null)),
            deadline: TimeSpan.Zero,
            CancellationToken.None,
            RecordingInstantDelay([]));

        var realError = await OperationPoller.PollAsync<string>(
            _ => Task.FromResult(new OperationPoll<string>(true, null, new CloudError("QUOTA_EXCEEDED", null, "no quota"))),
            deadline: TimeSpan.Zero,
            CancellationToken.None,
            RecordingInstantDelay([]));

        CloudErrorClassifier.Classify(timedOut.Error!).ShouldBe(CloudErrorKind.Network);
        CloudErrorClassifier.Classify(realError.Error!).ShouldBe(CloudErrorKind.Quota);
    }

    [Fact]
    public async Task A_cancelled_token_stops_polling_instead_of_waiting_out_the_deadline()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => OperationPoller.PollAsync<string>(
            _ => Task.FromResult(new OperationPoll<string>(false, null, null)),
            deadline: TimeSpan.FromMinutes(5),
            cts.Token,
            RecordingInstantDelay([])));
    }
}
