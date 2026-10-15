using DnaEntropyGraph.Cloud;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;

namespace DnaEntropyGraph.App.Services;

/// <summary>
/// Whether the app is talking to the development fake instead of Google Cloud (issue #644's read-only note; #69 owns the mode
/// itself). It follows the same SWITCH POINT as the token refresher in ServiceRegistration: the refresher is the fake exactly
/// while every gateway is the fake, so the note flips on its own when the first real gateway is wired.
/// </summary>
public sealed class FakeCloudModeInfo(ICloudTokenRefresher refresher) : ICloudModeInfo
{
    public bool IsFakeCloud => refresher is FakeGcp;
}
