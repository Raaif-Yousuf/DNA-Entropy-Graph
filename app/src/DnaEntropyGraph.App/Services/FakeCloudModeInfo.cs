using DnaEntropyGraph.App.Startup;
using DnaEntropyGraph.Core.Abstractions;

namespace DnaEntropyGraph.App.Services;

/// <summary>
/// Whether the app is talking to a simulated cloud instead of Google Cloud (issue #644's read-only note). It is the backend the
/// container was built over (<c>ServiceRegistration</c>'s one switch point, issues #609 and #69), not an inference from which
/// object some service happens to be: every backend except <see cref="CloudBackend.Google"/> is simulated, so a new gateway or a
/// changed default cannot make the note wrong.
/// </summary>
public sealed class FakeCloudModeInfo(CloudBackend backend) : ICloudModeInfo
{
    public bool IsFakeCloud => backend != CloudBackend.Google;
}
