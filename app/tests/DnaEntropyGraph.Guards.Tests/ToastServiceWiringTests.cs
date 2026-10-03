using DnaEntropyGraph.App.Startup;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// Issue #585: <c>IToastService</c> was registered as a Debug-output-only placeholder, so every message a ViewModel raised
/// showed nothing in a release build. This resolves the REAL registration and proves a message shown through it lands in the
/// state the shell's InfoBar binds to. Re-registering any service that does not write to the shell fails it.
/// </summary>
public class ToastServiceWiringTests
{
    [Fact]
    public void A_message_shown_through_the_registered_IToastService_is_visible_in_the_shell()
    {
        var services = new ServiceCollection();
        var root = Path.Combine(Path.GetTempPath(), $"deg-guard-toast-{Guid.NewGuid():n}");
        services.AddDnaEntropyGraph(appDataRoot: root);
        using var provider = services.BuildServiceProvider();

        var toasts = provider.GetRequiredService<IToastService>();
        var shell = provider.GetRequiredService<ShellViewModel>();
        shell.Messages.IsOpen.ShouldBeFalse("vacuity guard: nothing is showing before the call");

        toasts.ShowToast("Run deleted", "Its files were removed.", ToastSeverity.Error);

        shell.Messages.IsOpen.ShouldBeTrue("the registered IToastService must write to the shell's message state");
        shell.Messages.Title.ShouldBe("Run deleted");
        shell.Messages.Message.ShouldBe("Its files were removed.");
        shell.Messages.Severity.ShouldBe(ToastSeverity.Error);
    }
}
