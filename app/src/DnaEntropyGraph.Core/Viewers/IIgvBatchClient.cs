namespace DnaEntropyGraph.Core.Viewers;

/// <summary>How a batch run against IGV's port ended.</summary>
public enum IgvBatchOutcome
{
    /// <summary>Every command was answered OK.</summary>
    Done,

    /// <summary>Nothing accepted the connection: IGV is not running, or its batch port is off or on another number.</summary>
    NotListening,

    /// <summary>IGV answered a command with something other than OK; later commands were not sent.</summary>
    Rejected,

    /// <summary>Connected, but IGV did not answer a command in time.</summary>
    NoReply,
}

/// <summary>Talks to a running IGV desktop over its batch port (issue #586). Behind an interface so ViewModels never open a socket.</summary>
public interface IIgvBatchClient
{
    /// <summary>
    /// Sends <paramref name="commands"/> to 127.0.0.1:<paramref name="port"/>, one at a time, reading IGV's one-line answer
    /// to each before sending the next. Never throws for a connection or protocol problem: it returns the outcome.
    /// </summary>
    Task<IgvBatchOutcome> SendAsync(int port, IReadOnlyList<string> commands, CancellationToken cancellationToken);
}
