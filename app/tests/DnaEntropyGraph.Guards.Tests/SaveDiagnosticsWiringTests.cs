using System.Xml.Linq;
using DnaEntropyGraph.Presentation.ViewModels;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// Issue #106 (Hard Rule 13): several error strings tell the user to "Choose Save diagnostics in Settings". That is
/// only true while a button with exactly that label exists on a page the Settings nav item reaches, bound to a real command.
/// </summary>
public class SaveDiagnosticsWiringTests
{
    private static Dictionary<string, string> Resw() =>
        RepoPaths.AllReswFiles
            .SelectMany(file => XDocument.Load(file).Descendants("data"))
            .ToDictionary(d => d.Attribute("name")!.Value, d => d.Element("value")?.Value ?? string.Empty);

    private static string Read(params string[] relative) => File.ReadAllText(Path.Combine([RepoPaths.AppRoot, "src", .. relative]));

    [Fact]
    public void Every_string_that_sends_the_user_to_Save_diagnostics_matches_a_real_button_label()
    {
        var resw = Resw();
        var senders = resw.Where(entry => entry.Value.Contains("Save diagnostics", StringComparison.Ordinal)
                                          && !entry.Key.StartsWith("Diagnostics", StringComparison.Ordinal)
                                          && entry.Key != "SaveDiagnosticsButton.Content").ToList();

        senders.Count.ShouldBeGreaterThanOrEqualTo(4, "vacuity: the run-error strings that mention it");
        resw["SaveDiagnosticsButton.Content"].ShouldBe(SettingsViewModel.SaveDiagnosticsLabel);
        senders.ShouldAllBe(entry => entry.Value.Contains($"Choose {SettingsViewModel.SaveDiagnosticsLabel} in Settings", StringComparison.Ordinal));
    }

    [Fact]
    public void The_picker_label_and_the_default_file_name_prefix_come_from_resw_not_from_code()
    {
        var resw = Resw();

        resw["DiagnosticsFileNamePrefix"].ShouldBe("dna-entropy-diagnostics");
        resw["DiagnosticsFileTypeLabel"].Trim().ShouldNotBeEmpty();
        Read("DnaEntropyGraph.App", "Services", "FilePickerService.cs").ShouldContain("GetString(\"DiagnosticsFileTypeLabel\")");
        Read("DnaEntropyGraph.Presentation", "ViewModels", "SettingsViewModel.cs").ShouldContain("GetString(\"DiagnosticsFileNamePrefix\")");
    }

    [Fact]
    public void The_Settings_page_binds_the_command_under_that_label_and_the_Settings_route_reaches_it()
    {
        var page = Read("DnaEntropyGraph.App", "Views", "SettingsPage.xaml");
        page.ShouldContain("x:Uid=\"SaveDiagnosticsButton\"");
        page.ShouldContain("Command=\"{x:Bind ViewModel.SaveDiagnosticsCommand}\"");

        Read("DnaEntropyGraph.App", "Startup", "NavigationRoutes.cs").ShouldContain("RegisterPage(\"Settings\", typeof(SettingsPage))");
        Read("DnaEntropyGraph.App", "MainWindow.xaml").ShouldContain("Tag=\"Settings\"");
    }
}
