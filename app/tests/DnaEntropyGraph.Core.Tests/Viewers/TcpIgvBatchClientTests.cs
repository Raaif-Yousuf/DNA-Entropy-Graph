using System.Net;
using System.Net.Sockets;
using System.Text;
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
            var client = new TcpIgvBatchClient(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));

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
        var client = new TcpIgvBatchClient(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));

        (await client.SendAsync(port, ["new"], TestContext.Current.CancellationToken)).ShouldBe(IgvBatchOutcome.NotListening);
    }

    [Fact]
    public async Task An_answer_other_than_OK_stops_the_run_and_is_reported_as_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var (listener, port) = Listen();
        try
        {
            var server = Serve(listener, line => line.StartsWith("genome", StringComparison.Ordinal) ? "ERROR: bad genome" : "OK", ct);
            var client = new TcpIgvBatchClient(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));

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
            var client = new TcpIgvBatchClient(TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(300));

            (await client.SendAsync(port, ["new"], ct)).ShouldBe(IgvBatchOutcome.NoReply);
        }
        finally
        {
            listener.Stop();
        }
    }
}
