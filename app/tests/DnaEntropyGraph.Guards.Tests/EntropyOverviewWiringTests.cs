using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// Issue #118: the overview chart, the PNG button and the sparklines each rest on one XAML line and one DI line. A chart bound to a
/// property nobody fills, or a service nobody registers, shows nothing and fails nothing.
/// </summary>
public sealed class EntropyOverviewWiringTests
{
    private static string App(string relative) => File.ReadAllText(Path.Combine(RepoPaths.AppRoot, "src", "DnaEntropyGraph.App", relative.Replace('/', Path.DirectorySeparatorChar)));

    [Theory]
    [InlineData("Views/ResultsPage.xaml", "Contig=\"{x:Bind ViewModel.Overview, Mode=OneWay}\"")]
    [InlineData("Views/ResultsPage.xaml", "Command=\"{x:Bind ViewModel.ExportOverviewPngCommand}\"")]
    [InlineData("Views/ResultsPage.xaml", "RangeSelectedCommand=\"{x:Bind ViewModel.SelectOverviewRangeCommand}\"")]
    [InlineData("Views/RunsPage.xaml", "Values=\"{x:Bind Sparkline, Mode=OneWay}\"")]
    [InlineData("Views/RunsPage.xaml", "Visibility=\"{x:Bind HasSparkline, Mode=OneWay}\"")]
    public void The_XAML_binds_the_overview_pieces(string file, string fragment)
        => App(file).ShouldContain(fragment);

    [Theory]
    [InlineData("IEntropyOverviewSource, DnaEntropyGraph.Core.Tracks.EntropyOverviewSource")]
    [InlineData("IOverviewImageExporter, Services.OverviewPngExporter")]
    public void The_services_are_registered(string fragment)
        => App("Startup/ServiceRegistration.cs").ShouldContain(fragment);

    [Fact]
    public void Only_the_App_project_references_the_chart_library()
    {
        var offenders = RepoPaths.AllCsprojFiles
            .Where(p => File.ReadAllText(p).Contains("ScottPlot", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        offenders.ShouldBe(["DnaEntropyGraph.App.csproj"]);
    }
}
