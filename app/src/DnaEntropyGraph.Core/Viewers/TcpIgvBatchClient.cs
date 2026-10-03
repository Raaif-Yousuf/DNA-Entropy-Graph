using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DnaEntropyGraph.Core.Viewers;

/// <summary>
/// The real IGV batch client (issue #586). Loopback only. A <c>genome</c> command on a big FASTA can take a while,
/// so the reply timeout is generous; a refused connection is immediate.
/// </summary>
public sealed class TcpIgvBatchClient : IIgvBatchClient
{
    private readonly TimeSpan _connectTimeout;
    private readonly TimeSpan _replyTimeout;

    public TcpIgvBatchClient()
        : this(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(60))
    {
    }

    public TcpIgvBatchClient(TimeSpan connectTimeout, TimeSpan replyTimeout)
    {
        _connectTimeout = connectTimeout;
        _replyTimeout = replyTimeout;
    }

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
            return IgvBatchOutcome.NotListening;
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
                wait.CancelAfter(_replyTimeout);
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
