using System.Net;
using System.Net.Sockets;
using System.Text;
using DnaEntropyGraph.Cloud.Tests;
using DnaEntropyGraph.Core.Viewers;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests.Viewers;

/// <summary>Issue #586: the real socket client against a loopback listener posing as IGV.</summary>
public sealed class TcpIgvBatchClientTests
{
    private static (TcpListener Listener, int Port) Listen()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return (listener, ((IPEndPoint)listener.LocalEndpoint).Port);
    }

    // The client's connect and reply waits run on a clock that stands still (#841): a wait ends only when a test moves it, so a loaded machine
    // can never cut a reply that was on its way, and a test never sleeps to let a wait elapse.
    private static TcpIgvBatchClient Client(VirtualTimeProvider clock, TimeSpan reply, TimeSpan? load = null)
        => new(TimeSpan.FromSeconds(5), reply, load ?? reply, clock);

    private static async Task<List<string>> Serve(TcpListener listener, Func<string, string?> reply, CancellationToken ct)
    {
        var received = new List<string>();
        using var client = await listener.AcceptTcpClientAsync(ct);
        using var reader = new StreamReader(client.GetStream(), Encoding.UTF8);
        await using var writer = new StreamWriter(client.GetStream(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            received.Add(line);
            if (reply(line) is { } answer)
            {
                await writer.WriteLineAsync(answer.AsMemory(), ct);
            }
        }

        return received;
    }

    [Fact]
    public async Task Each_command_is_sent_only_after_the_previous_one_was_answered()
    {
        var ct = TestContext.Current.CancellationToken;
        var (listener, port) = Listen();
        try
        {
            var server = Serve(listener, _ => "OK", ct);
            var client = Client(new VirtualTimeProvider(), TimeSpan.FromSeconds(5));

            var result = await client.SendAsync(port, ["new", "genome \"a\"", "load \"b\""], ct);

            result.ShouldBe(IgvBatchOutcome.Done);
            (await server).ShouldBe(["new", "genome \"a\"", "load \"b\""]);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task A_port_nobody_listens_on_is_reported_as_not_listening()
    {
        var (listener, port) = Listen();
        listener.Stop();
        // MEASURED 2026-10-03: Windows reports a refused loopback connect after about 2.07 s. The connect timeout runs on a clock nobody moves, so it can never beat the refusal.
        var client = Client(new VirtualTimeProvider(), TimeSpan.FromSeconds(2));

        (await client.SendAsync(port, ["new"], TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken)).ShouldBe(IgvBatchOutcome.NotListening);
    }

    [Fact]
    public async Task An_answer_other_than_OK_stops_the_run_and_is_reported_as_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var (listener, port) = Listen();
        try
        {
            var server = Serve(listener, line => line.StartsWith("genome", StringComparison.Ordinal) ? "ERROR: bad genome" : "OK", ct);
            var client = Client(new VirtualTimeProvider(), TimeSpan.FromSeconds(5));

            var result = await client.SendAsync(port, ["new", "genome \"a\"", "load \"b\""], ct);

            result.ShouldBe(IgvBatchOutcome.Rejected);
            (await server).ShouldBe(["new", "genome \"a\""]);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task A_listener_that_never_answers_is_reported_as_no_reply_after_the_timeout()
    {
        var ct = TestContext.Current.CancellationToken;
        var (listener, port) = Listen();
        try
        {
            _ = Serve(listener, _ => null, ct);
            var clock = new VirtualTimeProvider();
            var client = Client(clock, TimeSpan.FromMilliseconds(300));
            var send = client.SendAsync(port, ["new"], ct);

            // The 300 ms reply wait is armed after the command is written; time passes only once it is.
            await clock.WhenTimerCreatedAsync(1, TimeSpan.FromMilliseconds(300)).WaitAsync(TimeSpan.FromSeconds(20), ct);
            clock.Advance(TimeSpan.FromMilliseconds(300));

            (await send).ShouldBe(IgvBatchOutcome.NoReply);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void Only_a_refused_connection_means_not_listening()
    {
        TcpIgvBatchClient.ClassifyConnectFailure(new SocketException((int)SocketError.ConnectionRefused)).ShouldBe(IgvBatchOutcome.NotListening);

        // A timeout (IGV busy loading) and any other failure must not start a second IGV.
        TcpIgvBatchClient.ClassifyConnectFailure(new OperationCanceledException()).ShouldBe(IgvBatchOutcome.NoConnection);
        TcpIgvBatchClient.ClassifyConnectFailure(new SocketException((int)SocketError.TimedOut)).ShouldBe(IgvBatchOutcome.NoConnection);
        TcpIgvBatchClient.ClassifyConnectFailure(new SocketException((int)SocketError.NetworkUnreachable)).ShouldBe(IgvBatchOutcome.NoConnection);
    }
    [Fact]
    public async Task A_slow_genome_or_load_gets_the_long_wait_but_a_slow_new_gets_the_short_one()
    {
        var ct = TestContext.Current.CancellationToken;
        var (listener, port) = Listen();
        SemaphoreSlim? release = null;
        try
        {
            // genome and load answer only once the test lets them, after the clock has moved past the 250 ms wait for new and short of the long wait.
            // Not disposed: the Serve callback can still be in Wait after an early failure, and disposing under it would throw in a discarded task. Released in the finally below instead.
            release = new SemaphoreSlim(0);
            var server = Serve(listener, line =>
            {
                if (!line.StartsWith("new", StringComparison.Ordinal))
                {
                    release.Wait(TimeSpan.FromSeconds(20));
                }

                return "OK";
            }, ct);
            var clock = new VirtualTimeProvider();
            var client = Client(clock, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5));
            var send = client.SendAsync(port, ["new", "genome \"a\"", "load \"b\""], ct);

            // Timers so far: connect (1), new's reply wait (2), genome's long wait (3).
            await clock.WhenTimerCreatedAsync(2, TimeSpan.FromSeconds(5)).WaitAsync(TimeSpan.FromSeconds(20), ct);
            clock.Advance(TimeSpan.FromSeconds(1));
            release.Release();
            await clock.WhenTimerCreatedAsync(3, TimeSpan.FromSeconds(5)).WaitAsync(TimeSpan.FromSeconds(20), ct);
            clock.Advance(TimeSpan.FromSeconds(1));
            release.Release();

            (await send).ShouldBe(IgvBatchOutcome.Done);
            _ = server;
        }
        finally
        {
            release?.Release(10);
            listener.Stop();
        }
    }

    [Fact]
    public async Task A_new_that_takes_longer_than_the_short_wait_is_no_reply()
    {
        var ct = TestContext.Current.CancellationToken;
        var (listener, port) = Listen();
        try
        {
            _ = Serve(listener, _ => null, ct);
            var clock = new VirtualTimeProvider();
            var client = Client(clock, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5));
            var send = client.SendAsync(port, ["new"], ct);

            await clock.WhenTimerCreatedAsync(1, TimeSpan.FromMilliseconds(250)).WaitAsync(TimeSpan.FromSeconds(20), ct);
            clock.Advance(TimeSpan.FromMilliseconds(250));

            (await send).ShouldBe(IgvBatchOutcome.NoReply);
        }
        finally
        {
            listener.Stop();
        }
    }
    [Fact]
    public async Task Cancelling_while_waiting_for_a_reply_cancels_the_send()
    {
        var ct = TestContext.Current.CancellationToken;
        var (listener, port) = Listen();
        try
        {
            _ = Serve(listener, _ => null, ct);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var clock = new VirtualTimeProvider();
            var client = Client(clock, TimeSpan.FromSeconds(30));
            var send = client.SendAsync(port, ["new"], cts.Token);
            await clock.WhenTimerCreatedAsync(1, TimeSpan.FromSeconds(30)).WaitAsync(TimeSpan.FromSeconds(20), ct); // the client is now waiting for a reply
            await cts.CancelAsync();

            await Should.ThrowAsync<OperationCanceledException>(send);
        }
        finally
        {
            listener.Stop();
        }
    }
}
