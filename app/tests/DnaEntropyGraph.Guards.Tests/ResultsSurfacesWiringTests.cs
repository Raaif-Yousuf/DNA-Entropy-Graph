using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// Issue #552: the gene table and the smoothed-track switches each rest on XAML lines and one DI line. A table bound to a collection
/// nobody fills, a switch bound to nothing, or a source nobody registers, shows nothing and fails nothing.
/// </summary>
public sealed class ResultsSurfacesWiringTests
{
    private static string App(string relative) => File.ReadAllText(Path.Combine(RepoPaths.AppRoot, "src", "DnaEntropyGraph.App", relative.Replace('/', Path.DirectorySeparatorChar)));

    [Theory]
    [InlineData("Views/ResultsPage.xaml", "ItemsSource=\"{x:Bind ViewModel.GeneTables}\"")]
    [InlineData("Views/ResultsPage.xaml", "Visibility=\"{x:Bind ViewModel.HasGeneTables, Mode=OneWay}\"")]
    [InlineData("Views/ResultsPage.xaml", "Message=\"{x:Bind ViewModel.GeneNoticeText, Mode=OneWay}\"")]
    [InlineData("Views/ResultsPage.xaml", "ItemsSource=\"{x:Bind Rows}\"")]
    [InlineData("Views/ResultsPage.xaml", "Command=\"{x:Bind SortByCommand}\" CommandParameter=\"mean\"")]
    [InlineData("Views/ViewerPage.xaml", "ItemsSource=\"{x:Bind ViewModel.SmoothedTracks}\"")]
    [InlineData("Views/ViewerPage.xaml", "IsChecked=\"{x:Bind IsOn, Mode=TwoWay}\"")]
    [InlineData("Views/ViewerPage.xaml", "IsEnabled=\"{x:Bind ViewModel.AreSmoothedTracksEnabled, Mode=OneWay}\"")]
    public void The_XAML_binds_the_surfaces(string file, string fragment)
        => App(file).ShouldContain(fragment);

    [Fact]
    public void Every_gene_column_heading_sorts_by_its_own_key_and_has_its_text()
    {
        var xaml = App("Views/ResultsPage.xaml");
        var resw = App("Strings/en-US/Resources.resw");
        foreach (var key in DnaEntropyGraph.Presentation.ViewModels.ResultsCopy.GeneColumnKeys)
        {
            xaml.ShouldContain($"CommandParameter=\"{key}\"", customMessage: key);
        }

        foreach (var uid in new[] { "Contig", "Gene", "Begin", "End", "Strand", "Length", "Mean", "Min", "Max", "Low" })
        {
            xaml.ShouldContain($"x:Uid=\"ResultsGeneHead{uid}\"");
            resw.ShouldContain($"name=\"ResultsGeneHead{uid}.Content\"");
        }
    }

    [Fact]
    public void The_gene_table_source_is_registered()
        => App("Startup/ServiceRegistration.cs").ShouldContain("IGeneTableSource, DnaEntropyGraph.Core.Tracks.GeneTableSource");

    [Fact]
    public void The_bridge_handles_the_track_command_the_viewer_model_sends()
    {
        var js = App("Assets/viewer/bridge.js");
        js.ShouldContain("case 'track':");
        js.ShouldContain("removeTrackByName");
        js.ShouldContain("loadTrack");
    }
}
