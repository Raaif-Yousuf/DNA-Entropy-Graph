using DnaEntropyGraph.App.Startup;
using DnaEntropyGraph.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// Review of #173, 2026-10-15: Settings and New run each built their own picker, so a choice made in Settings was not what an already-open
/// New run page used. One registered instance is what makes them follow each other; this fails if either is made transient again.
/// </summary>
public class RunTargetPickerSharingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "deg-picker-share-" + Guid.NewGuid().ToString("N"));

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
    public void Settings_and_New_run_use_the_same_picker_so_a_choice_made_in_Settings_is_what_the_next_run_uses()
    {
        var services = new ServiceCollection();
        services.AddDnaEntropyGraph(appDataRoot: _root, cloud: CloudBackend.Fake);
        using var provider = services.BuildServiceProvider();
        var settings = provider.GetRequiredService<ThisPcSettingsViewModel>();
        var newRun = provider.GetRequiredService<NewRunViewModel>();

        newRun.RunTarget.ShouldNotBeNull();
        newRun.RunTarget.ShouldBeSameAs(settings.Picker);

        settings.Picker.SelectedTarget = "Auto";

        newRun.RunTarget!.SelectedTarget.ShouldBe("Auto");
    }
}
