using System.Reflection;
using DnaEntropyGraph.App.Startup;
using DnaEntropyGraph.Presentation.Services;
using DnaEntropyGraph.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>Issue #643's wired-to-nothing observable on the real container: the shell is built with the registered completion actions, so a finished run reaches the Notifications settings.</summary>
public sealed class ShellCompletionWiringTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "deg-shell-completion-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void The_shell_holds_the_registered_RunCompletionActions_instance()
    {
        var services = new ServiceCollection();
        services.AddDnaEntropyGraph(appDataRoot: _root);
        using var provider = services.BuildServiceProvider();

        var shell = provider.GetRequiredService<ShellViewModel>();

        var held = typeof(ShellViewModel).GetField("_completion", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(shell);
        held.ShouldNotBeNull();
        held.ShouldBeSameAs(provider.GetRequiredService<RunCompletionActions>());
    }
}
