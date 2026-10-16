using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DnaEntropyGraph.Presentation.Services;

namespace DnaEntropyGraph.Presentation.ViewModels;

/// <summary>
/// The first-launch tour (issue #143): four steps (New run, Runs, Cloud, Settings), one tip open at a time. The window's TeachingTips bind
/// the four <c>Is...StepOpen</c> flags, Next and Skip; Settings replays it with <see cref="StartCommand"/>. The step text lives in Resources.resw.
/// The tips point at navigation items, so none is shown while the navigation pane is hidden (a narrow window): the tour waits, and resumes
/// at the same step when the pane comes back (<see cref="SetNavigationMinimal"/>).
/// </summary>
public sealed partial class TourViewModel : ObservableObject
{
    private const int StepCount = 4;

    /// <summary>TeachingTipCloseReason.LightDismiss: the user closed the tip the framework's way (outside click, Esc) instead of with its buttons.</summary>
    public const int LightDismissReason = 1;

    private readonly TourSettings _settings;

    /// <summary>The current step (0 to 3), or -1 when the tour is not running.</summary>
    private int _step = -1;

    private bool _navigationMinimal;

    public TourViewModel(TourSettings settings, BuildIdentity? build = null)
    {
        _settings = settings;
        GuideIndexUri = UserGuideLinks.UriFor(string.Empty, build);
    }

    /// <summary>The user guide index, for the Settings link next to the replay button.</summary>
    public Uri GuideIndexUri { get; }

    /// <summary>The last settings write started by finishing or skipping; tests await it.</summary>
    public Task PersistWork { get; private set; } = Task.CompletedTask;

    public bool IsRunning => _step >= 0;

    public bool IsNewRunStepOpen => _step == 0 && !_navigationMinimal;

    public bool IsRunsStepOpen => _step == 1 && !_navigationMinimal;

    public bool IsCloudStepOpen => _step == 2 && !_navigationMinimal;

    public bool IsSettingsStepOpen => _step == 3 && !_navigationMinimal;

    /// <summary>
    /// Called once the window is up: opens the tour on a profile that has not seen it. The settings file is read on the thread pool (the
    /// store is a mutex with a short wait) and the tour opens back on the calling thread.
    /// </summary>
    public async Task StartIfFirstLaunchAsync()
    {
        var seen = await Task.Run(() => _settings.Completed).ConfigureAwait(true);
        if (!seen)
        {
            SetStep(0);
        }
    }

    /// <summary>The navigation pane was hidden (a narrow window) or shown again; the tips follow it.</summary>
    public void SetNavigationMinimal(bool minimal)
    {
        _navigationMinimal = minimal;
        NotifyFlags();
    }

    /// <summary>A tip closed by the framework (<see cref="LightDismissReason"/>) ends the tour, so the view never shows a step the user closed. Closing by its own buttons is handled by Next and Skip.</summary>
    public void OnTipClosed(int reason)
    {
        if (reason == LightDismissReason)
        {
            Skip();
        }
    }

    /// <summary>Starts (or restarts) the tour from the first step; Settings uses it to replay.</summary>
    [RelayCommand]
    private void Start() => SetStep(0);

    [RelayCommand]
    private void Next()
    {
        if (!IsRunning)
        {
            return;
        }

        if (_step + 1 >= StepCount)
        {
            Finish();
        }
        else
        {
            SetStep(_step + 1);
        }
    }

    [RelayCommand]
    private void Skip()
    {
        if (IsRunning)
        {
            Finish();
        }
    }

    private void Finish()
    {
        SetStep(-1);

        // Written off the UI thread; a failed write is the settings class's to swallow.
        PersistWork = Task.Run(_settings.MarkCompleted);
    }

    private void SetStep(int step)
    {
        _step = step;
        OnPropertyChanged(nameof(IsRunning));
        NotifyFlags();
    }

    private void NotifyFlags()
    {
        OnPropertyChanged(nameof(IsNewRunStepOpen));
        OnPropertyChanged(nameof(IsRunsStepOpen));
        OnPropertyChanged(nameof(IsCloudStepOpen));
        OnPropertyChanged(nameof(IsSettingsStepOpen));
    }
}
