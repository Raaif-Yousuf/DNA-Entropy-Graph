using System.Xml.Linq;
using DnaEntropyGraph.Core.Runs;
using DnaEntropyGraph.Presentation.ViewModels;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// Issue #934 review round 3: a refusal shown as a toast cannot carry an Open folder button (the toast service has none), so its wording names the path that really exists:
/// the Open button on the run's row, then the Open folder button on the page it opens. Each step is checked against the real resources and XAML.
/// </summary>
public sealed class RunsRefusalToastWordingTests
{
    private static Dictionary<string, string> Resw()
    {
        var resw = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in RepoPaths.AllReswFiles)
        {
            foreach (var data in XDocument.Load(file).Descendants("data"))
            {
                resw[data.Attribute("name")!.Value] = data.Element("value")?.Value ?? string.Empty;
            }
        }

        return resw;
    }

    [Fact]
    public void Every_toast_variant_names_the_Open_button_then_the_Open_folder_button_and_each_exists()
    {
        var resw = Resw();
        var bodies = new[]
        {
            RunsCopy.DeleteLocalAsToast(LocalDeleteStatus.OtherRunsFolder).Body,
            RunsCopy.DeleteLocalAsToast(LocalDeleteStatus.DriveRootRefused).Body,
            RunsCopy.DeleteLocalAsToast(LocalDeleteStatus.ProtectedFolderRefused).Body,
            RunsCopy.RedownloadDriveRootAsToast.Body,
        };

        foreach (var key in bodies)
        {
            key.ShouldEndWith("_Toast_Body");
            resw[key].ShouldContain("Choose Open on this run, then Open folder on the page that appears", Case.Sensitive, key);
            resw[key].ShouldNotContain("—");
        }

        // The two buttons the wording names are really there, with exactly those labels, and the second is bound to a command.
        resw["RunsOpenButton.Content"].ShouldBe("Open");
        resw["ResultsOpenFolderButton.Content"].ShouldBe("Open folder");
        var runsPage = File.ReadAllText(RepoPaths.AllXamlFiles.Single(f => Path.GetFileName(f) == "RunsPage.xaml"));
        runsPage.ShouldContain("x:Uid=\"RunsOpenButton\" Command=\"{x:Bind OpenCommand}\"");
        var resultsPage = File.ReadAllText(RepoPaths.AllXamlFiles.Single(f => Path.GetFileName(f) == "ResultsPage.xaml"));
        resultsPage.ShouldContain("x:Uid=\"ResultsOpenFolderButton\" Command=\"{x:Bind ViewModel.OpenFolderCommand}\"");
    }
}
