using DnaEntropyGraph.Core.Runs;

namespace DnaEntropyGraph.App.Services;

/// <summary>
/// The stand-in <see cref="IJobObjectDeleter"/> until the cloud lane adds listing and deleting objects to
/// the storage gateway (the area:cloud issue filed with #101). It deletes nothing and says so, so a
/// "Delete cloud copy" press never reports success it did not achieve.
/// </summary>
public sealed class UnconnectedJobObjectDeleter : IJobObjectDeleter
{
    public Task DeleteJobObjectsAsync(string bucket, string jobPrefix, CancellationToken cancellationToken)
        => throw new CloudNotConnectedException();
}
