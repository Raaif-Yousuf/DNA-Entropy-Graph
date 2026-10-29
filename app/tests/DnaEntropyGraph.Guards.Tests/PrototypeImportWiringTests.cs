using DnaEntropyGraph.App.Startup;
using DnaEntropyGraph.Core.PrototypeImport;
using DnaEntropyGraph.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// Issue #243, wired-to-nothing: the offer only happens if the shell is given the view model, the window asks for it after the first
/// navigation, and the real search paths are the prototype's own (MEASURED from worker/legacy: %APPDATA%\dna-entropy and the
/// user-profile Downloads folder). Any one missing compiles, passes the unit tests and never offers anything.
/// </summary>
public sealed class PrototypeImportWiringTests
{
    [Fact]
    public void The_shell_takes_the_import_view_model_and_it_resolves_with_the_real_search_paths()
    {
        var services = new ServiceCollection();
        services.AddDnaEntropyGraph(appDataRoot: Path.Combine(Path.GetTempPath(), $"deg-guard-proto-{Guid.NewGuid():n}"));
        using var provider = services.BuildServiceProvider();

        typeof(ShellViewModel).GetConstructors().Single().GetParameters().ShouldContain(p => p.ParameterType == typeof(PrototypeImportViewModel));
        provider.GetRequiredService<PrototypeImportViewModel>().ShouldBeSameAs(provider.GetRequiredService<PrototypeImportViewModel>());
        var paths = provider.GetRequiredService<PrototypeSearchPaths>();
        paths.ConfigPath.ShouldEndWith(Path.Combine("dna-entropy", "config.json"));
        paths.Downloads.ShouldEndWith("Downloads");
        paths.ConfigPath.ShouldStartWith(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
    }

    [Fact]
    public void The_main_window_runs_the_startup_offers_after_the_shell_is_ready()
    {
        var source = File.ReadAllText(Path.Combine(RepoPaths.AppRoot, "src", "DnaEntropyGraph.App", "MainWindow.xaml.cs"));

        source.IndexOf("RunStartupOffersAsync", StringComparison.Ordinal).ShouldBeGreaterThan(source.IndexOf("MarkReady", StringComparison.Ordinal));
    }

    [Fact]
    public void The_settings_page_binds_the_button_to_the_command()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoPaths.AppRoot, "src", "DnaEntropyGraph.App", "Views", "SettingsPage.xaml"));

        xaml.ShouldContain("PrototypeImport.FindCommand");
    }
}
