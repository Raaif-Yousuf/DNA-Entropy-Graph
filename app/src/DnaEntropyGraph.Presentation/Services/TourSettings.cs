using DnaEntropyGraph.Core.Abstractions;

namespace DnaEntropyGraph.Presentation.Services;

/// <summary>
/// Whether the first-launch tour has been seen (issue #143). Read at start-up by <see cref="ViewModels.TourViewModel.StartIfFirstLaunch"/>;
/// written when the tour is finished or skipped. A locked settings file reads as seen (a tour that cannot remember would show on
/// every launch) and a failed write is ignored (the tour then shows once more).
/// </summary>
public sealed class TourSettings(ISettingsStore store)
{
    public const string CompletedKey = "onboarding_tour_done";

    public bool Completed
    {
        get
        {
            try
            {
                return bool.TryParse(store.GetString(CompletedKey), out var done) && done;
            }
            catch (SettingsUnavailableException)
            {
                return true;
            }
        }
    }

    public void MarkCompleted()
    {
        try
        {
            store.SetString(CompletedKey, "true");
        }
        catch (SettingsUnavailableException)
        {
            // The tour is closed either way; it may show once more next launch.
        }
    }
}
