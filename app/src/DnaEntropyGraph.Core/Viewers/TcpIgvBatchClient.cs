using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DnaEntropyGraph.Core.Viewers;

/// <summary>
/// The real IGV batch client (issue #586). Loopback only. A <c>genome</c> command on a big FASTA can take a while, so it gets
/// a longer reply wait than <c>new</c>, and so does <c>load</c> (a big track). Only a refused connection means "IGV is not running"; a connect that
/// times out or fails otherwise is <see cref="IgvBatchOutcome.NoConnection"/> so the caller never starts a second IGV.
/// </summary>
public sealed class TcpIgvBatchClient : IIgvBatchClient
{
    // MEASURED 2026-10-03: on Windows a connect to a closed loopback port takes about 2.07 s to report ConnectionRefused (the SYN is
    // retried once). A connect timeout of 2 s or less would therefore turn every "IGV is not running" into a timeout and IGV would
    // never be started, so the wait is comfortably longer. A listening IGV accepts at once.
    private static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(6);

    private readonly TimeSpan _connectTimeout;
    private readonly TimeSpan _replyTimeout;
    private readonly TimeSpan _loadReplyTimeout;

    public TcpIgvBatchClient()
        : this(DefaultConnectTimeout, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(60))
    {
    }

    public TcpIgvBatchClient(TimeSpan connectTimeout, TimeSpan replyTimeout)
        : this(connectTimeout, replyTimeout, replyTimeout)
    {
    }

    public TcpIgvBatchClient(TimeSpan connectTimeout, TimeSpan replyTimeout, TimeSpan loadReplyTimeout)
    {
        _connectTimeout = connectTimeout;
        _replyTimeout = replyTimeout;
        _loadReplyTimeout = loadReplyTimeout;
    }

    /// <summary>
    /// Only a refused connection proves nothing is listening. A timeout (IGV busy) or any other socket error leaves that open, so
    /// it is <see cref="IgvBatchOutcome.NoConnection"/> and the caller does not start a second IGV.
    /// </summary>
    public static IgvBatchOutcome ClassifyConnectFailure(Exception failure)
        => failure is SocketException { SocketErrorCode: SocketError.ConnectionRefused } ? IgvBatchOutcome.NotListening : IgvBatchOutcome.NoConnection;

    // new is instant; genome and load read the whole file, and a big bedGraph or GFF3 can take as long as a genome. A retry resends the
    // same commands, so a wait too short for the load would make that run unopenable.
    private static bool IsLoad(string command)
        => command.StartsWith("genome", StringComparison.Ordinal) || command.StartsWith("load", StringComparison.Ordinal);

    public async Task<IgvBatchOutcome> SendAsync(int port, IReadOnlyList<string> commands, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        try
        {
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connect.CancelAfter(_connectTimeout);
            await client.ConnectAsync(IPAddress.Loopback, port, connect.Token);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return ClassifyConnectFailure(ex);
        }

        try
        {
            var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, leaveOpen: true);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
            foreach (var command in commands)
            {
                await writer.WriteLineAsync(command.AsMemory(), cancellationToken);

                using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                wait.CancelAfter(IsLoad(command) ? _loadReplyTimeout : _replyTimeout);
                string? reply;
                try
                {
                    reply = await reader.ReadLineAsync(wait.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return IgvBatchOutcome.NoReply;
                }

                if (reply is null)
                {
                    return IgvBatchOutcome.NoReply;
                }

                if (!reply.Trim().Equals("OK", StringComparison.OrdinalIgnoreCase))
                {
                    return IgvBatchOutcome.Rejected;
                }
            }

            return IgvBatchOutcome.Done;
        }
        catch (IOException)
        {
            // IGV closed the connection mid-run.
            return IgvBatchOutcome.NoReply;
        }
    }
}


