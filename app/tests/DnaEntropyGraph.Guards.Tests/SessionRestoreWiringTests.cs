using DnaEntropyGraph.App.Startup;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Presentation.Services;
using DnaEntropyGraph.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// Issue #244, wired-to-nothing: the session is only remembered if the navigator every ViewModel receives is the recording one,
/// and only restored if the shell was given the restorer. Either missing compiles, passes the unit tests and restores nothing.
/// </summary>
public sealed class SessionRestoreWiringTests
{
    private static ServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddDnaEntropyGraph(appDataRoot: Path.Combine(Path.GetTempPath(), $"deg-guard-session-{Guid.NewGuid():n}"));
        return services.BuildServiceProvider();
    }

    [Fact]
    public void The_INavigator_every_ViewModel_gets_records_the_session()
    {
        using var provider = Build();

        provider.GetRequiredService<INavigator>().ShouldBeOfType<RecordingNavigator>();
    }

    [Fact]
    public void The_shell_restores_from_the_same_store_the_navigator_records_to()
    {
        using var provider = Build();

        provider.GetRequiredService<SessionStore>().ShouldBeSameAs(provider.GetRequiredService<SessionStore>());
        provider.GetService<SessionRestorer>().ShouldNotBeNull();
        // The shell takes the restorer as an optional constructor argument, which DI fills only when it is registered.
        typeof(ShellViewModel).GetConstructors().Single().GetParameters().ShouldContain(p => p.ParameterType == typeof(SessionRestorer));
    }

    [Fact]
    public void The_main_window_waits_for_the_session_before_selecting_a_page()
    {
        var source = File.ReadAllText(Path.Combine(RepoPaths.AppRoot, "src", "DnaEntropyGraph.App", "MainWindow.xaml.cs"));

        source.ShouldContain("PrepareSessionAsync");
        source.ShouldContain("StartupNavKey");
    }
}
