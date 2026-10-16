using DnaEntropyGraph.Presentation.Services;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// Issue #143: the Help button and the tour reach the user. Three ways this ships wired to nothing: a Help link to a guide file that
/// does not exist (a 404 on GitHub), a tour step nobody binds, and a window that never starts the tour.
/// </summary>
public sealed class UserGuideLinksTests
{
    private static string GuideFolder => Path.GetFullPath(Path.Combine(RepoPaths.AppRoot, "..", "docs", "user_guide"));

    private static string Read(string name) => File.ReadAllText(RepoPaths.AllXamlFiles.Single(p => Path.GetFileName(p) == name));

    [Fact]
    public void Every_guide_file_a_help_link_can_open_exists_in_the_repository()
    {
        UserGuideLinks.AllFiles.Count.ShouldBeGreaterThan(4, "vacuity: the list must name the pages");
        foreach (var file in UserGuideLinks.AllFiles)
        {
            File.Exists(Path.Combine(GuideFolder, file)).ShouldBeTrue($"{file} is linked from Help but is not in docs/user_guide");
        }

        foreach (var key in new[] { "NewRun", "Runs", "Cloud", "Settings", "Wizard", "RunProgress", "Results", "Viewer", "nope" })
        {
            UserGuideLinks.AllFiles.ShouldContain(UserGuideLinks.FileFor(key));
        }
    }

    [Fact]
    public void The_window_binds_the_help_link_and_every_tour_step_and_starts_the_tour_when_it_loads()
    {
        var xaml = Read("MainWindow.xaml");
        xaml.ShouldContain("ViewModel.HelpUri");
        foreach (var flag in new[] { "IsNewRunStepOpen", "IsRunsStepOpen", "IsCloudStepOpen", "IsSettingsStepOpen" })
        {
            xaml.ShouldContain("ViewModel.Tour." + flag);
        }

        var code = File.ReadAllText(RepoPaths.AllXamlCsFiles.Single(p => Path.GetFileName(p) == "MainWindow.xaml.cs"));
        code.ShouldContain("ViewModel.OnWindowLoadedAsync()");
        code.ShouldContain("ViewModel.OnNavigationDisplayModeChanged(");
        code.ShouldContain("ViewModel.OnTourTipClosed(");
        xaml.ShouldContain("DisplayModeChanged=\"RootNavigationView_DisplayModeChanged\"");
        xaml.Split("Closed=\"TourTip_Closed\"").Length.ShouldBe(5, "all four tips report a framework close");
    }

    [Fact]
    public void Settings_can_replay_the_tour()
    {
        Read("SettingsPage.xaml").ShouldContain("Tour.StartCommand");
    }

    [Fact]
    public void The_navigation_service_announces_the_page_on_forward_navigation_and_on_back()
    {
        var service = File.ReadAllText(RepoPaths.AllCSharpFiles.Single(p => Path.GetFileName(p) == "NavigationService.cs"));

        service.Split("CurrentPageChanged?.Invoke(").Length.ShouldBe(3, "NavigateTo and GoBack each raise it (#143): Help and the tour read the page that is really showing");
        service.ShouldContain("_history.Push(pageKey)");
        service.ShouldContain("_history.Pop()");
    }
}