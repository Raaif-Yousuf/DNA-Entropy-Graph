using DnaEntropyGraph.App.Startup;
using Microsoft.Windows.AppLifecycle;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.App.UiTests;

/// <summary>Issue #107: which text an activation carries, by its kind (the WinRT argument objects cannot be built in a test, so the choice is a pure function).</summary>
public sealed class DesktopActivationTests
{
    [Fact]
    public void A_notification_activation_carries_the_notification_argument()
        => DesktopActivation.ArgumentsFrom(ExtendedActivationKind.AppNotification, () => "run=j1", () => "ignored").ShouldBe("run=j1");

    [Fact]
    public void A_launch_activation_carries_the_command_line()
        => DesktopActivation.ArgumentsFrom(ExtendedActivationKind.Launch, () => "ignored", () => "\"C:\\a.gb\"").ShouldBe("\"C:\\a.gb\"");

    [Fact]
    public void Any_other_kind_carries_nothing_and_reads_neither_source()
        => DesktopActivation.ArgumentsFrom(ExtendedActivationKind.File, () => throw new InvalidOperationException(), () => throw new InvalidOperationException()).ShouldBeNull();
}
