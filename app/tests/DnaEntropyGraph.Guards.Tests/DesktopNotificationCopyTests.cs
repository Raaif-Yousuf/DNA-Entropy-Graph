using System.Xml.Linq;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>Issue #107 / Hard Rule 11: a failed run's notification must not promise what the app has not confirmed.</summary>
public class DesktopNotificationCopyTests
{
    [Fact]
    public void The_failed_run_notification_does_not_claim_charging_has_stopped()
    {
        // Teardown can fail after a failed run, so the VM may still be running; the claim needs a confirmed terminal state the notification does not have.
        var body = RepoPaths.AllReswFiles
            .SelectMany(f => XDocument.Load(f).Descendants("data"))
            .Single(d => d.Attribute("name")!.Value == "RunFailed_Body")
            .Element("value")!.Value;

        body.ToLowerInvariant().ShouldNotContain("charged");
        body.ToLowerInvariant().ShouldNotContain("nothing more");
        body.ToLowerInvariant().ShouldNotContain("stopped");
    }
}
