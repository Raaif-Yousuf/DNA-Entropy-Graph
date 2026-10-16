using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DnaEntropyGraph.Presentation.Services;

namespace DnaEntropyGraph.Presentation.ViewModels;

/// <summary>
/// The first-launch tour (issue #143): four steps (New run, Runs, Cloud, Settings), one tip open at a time. The window's TeachingTips bind
/// the four <c>Is...StepOpen</c> flags, Next and Skip; Settings replays it with <see cref="StartCommand"/>. The step text lives in Resources.resw.
/// </summary>
public sealed partial class TourViewModel : ObservableObject
{
    private readonly TourSettings _settings;

    private const int StepCount = 4;

    public TourViewModel(TourSettings settings) => _settings = settings;

    /// <summary>The open step (0 to 3), or -1 when the tour is not running.</summary>
    private int _step = -1;

    /// <summary>The user guide index, for the Settings link next to the replay button.</summary>
    public Uri GuideIndexUri { get; } = UserGuideLinks.UriFor(string.Empty);

    public bool IsRunning => _step >= 0;

    public bool IsNewRunStepOpen => _step == 0;

    public bool IsRunsStepOpen => _step == 1;

    public bool IsCloudStepOpen => _step == 2;

    public bool IsSettingsStepOpen => _step == 3;

    /// <summary>Called once the window is up: opens the tour on a profile that has not seen it.</summary>
    public void StartIfFirstLaunch()
    {
        if (!_settings.Completed)
        {
            SetStep(0);
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
        _settings.MarkCompleted();
    }

    private void SetStep(int step)
    {
        _step = step;
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsNewRunStepOpen));
        OnPropertyChanged(nameof(IsRunsStepOpen));
        OnPropertyChanged(nameof(IsCloudStepOpen));
        OnPropertyChanged(nameof(IsSettingsStepOpen));
    }
}
