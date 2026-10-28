using DnaEntropyGraph.App.Startup;
using DnaEntropyGraph.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>Issue #527: New run is one instance for the whole app, so the files on the page survive a trip to Runs and back (a new page resolves the same ViewModel).</summary>
public sealed class NewRunStateKeptTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "deg-newrun-di-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort
        }
    }

    [Fact]
    public void Every_New_run_page_gets_the_same_view_model_so_its_files_survive_navigation()
    {
        var services = new ServiceCollection();
        services.AddDnaEntropyGraph(appDataRoot: _root, cloud: CloudBackend.Fake);
        using var provider = services.BuildServiceProvider();

        var first = provider.GetRequiredService<NewRunViewModel>();
        var second = provider.GetRequiredService<NewRunViewModel>();

        second.ShouldBeSameAs(first);
    }
}
