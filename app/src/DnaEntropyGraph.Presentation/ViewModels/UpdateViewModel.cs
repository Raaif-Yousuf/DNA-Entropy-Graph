using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Presentation.Messaging;
using DnaEntropyGraph.Presentation.Services;

namespace DnaEntropyGraph.Presentation.ViewModels;

/// <summary>
/// The in-app update check (issues #179, #145): the InfoBar in the shell and the Settings "About" group share this one instance,
/// so a check from either shows in both. It never restarts the app while a run is active (<see cref="IActiveRunProbe"/>), checks
/// on launch only when the Settings switch is on, and keeps a failed launch check silent (only a press of Check now complains).
/// </summary>
public sealed partial class UpdateViewModel : ObservableObject
{
    private readonly IAppUpdateSource _source;
    private readonly UpdateSettings _settings;
    private readonly IStringResourceProvider _strings;
    private readonly IActiveRunProbe _runs;
    private readonly BuildIdentity? _build;
    private bool _loading;
    private bool _downloaded;

    /// <summary>The shell's "Update available" bar is showing. The bar's close button clears it; <see cref="HasUpdate"/> stays, so Settings can still apply.</summary>
    [ObservableProperty]
    private bool _isBannerOpen;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestartToUpdateCommand))]
    private bool _hasUpdate;

    /// <summary>The version on offer; a plain property because only the banner and status text (built from it) are shown.</summary>
    public string AvailableVersion { get; private set; } = string.Empty;

    [ObservableProperty]
    private string _bannerTitle = string.Empty;

    /// <summary>The GitHub release body of the offered update, shown in Settings so a user sees what changes before updating (#145).</summary>
    [ObservableProperty]
    private string _releaseNotes = string.Empty;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckNowCommand), nameof(RestartToUpdateCommand))]
    private bool _isBusy;

    /// <summary>The Settings channel radio position: 0 Stable, 1 Beta.</summary>
    [ObservableProperty]
    private int _channelIndex;

    [ObservableProperty]
    private bool _checkOnLaunch;

    public UpdateViewModel(IAppUpdateSource source, UpdateSettings settings, IStringResourceProvider strings, IActiveRunProbe runs, IMessenger? messenger = null, BuildIdentity? build = null)
    {
        _build = build;
        _source = source;
        _settings = settings;
        _strings = strings;
        _runs = runs;
        // Fields, not properties: opening the page must not save what was just read.
        _channelIndex = settings.Channel == UpdateChannel.Beta ? 1 : 0;
        _checkOnLaunch = settings.CheckOnLaunch;
        messenger?.Register<UpdateViewModel, SettingsResetMessage>(this, static (vm, _) => vm.Reload());
    }

    public string CurrentVersionText => string.Format(System.Globalization.CultureInfo.CurrentCulture, _strings.GetString("AboutVersion_Text"), _source.CurrentVersion);

    /// <summary>The exact build (version and short sha, issue #263) for the About group; empty when no build identity was given.</summary>
    public string BuildText => _build is null ? string.Empty : string.Format(System.Globalization.CultureInfo.CurrentCulture, _strings.GetString("AboutBuild_Text"), _build.Info.Display);

    private UpdateChannel Channel => ChannelIndex == 1 ? UpdateChannel.Beta : UpdateChannel.Stable;

    /// <summary>The launch check: silent when the switch is off or when it fails (no network is not the user's problem at start-up).</summary>
    public async Task CheckOnLaunchAsync()
    {
        if (!_settings.CheckOnLaunch)
        {
            return;
        }

        await RunCheckAsync(reportFailure: false).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private Task CheckNowAsync() => RunCheckAsync(reportFailure: true);

    private bool CanCheck() => !IsBusy;

    private async Task RunCheckAsync(bool reportFailure)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusText = reportFailure ? _strings.GetString("UpdateChecking") : string.Empty;
        try
        {
            var result = await _source.CheckAsync(Channel, CancellationToken.None).ConfigureAwait(true);
            switch (result.Outcome)
            {
                case UpdateCheckOutcome.Available when result.Update is { } update:
                    Offer(update);
                    break;
                case UpdateCheckOutcome.NotInstalled:
                    ClearOffer();
                    StatusText = reportFailure ? _strings.GetString("UpdateNotInstalled") : string.Empty;
                    break;
                default:
                    ClearOffer();
                    StatusText = reportFailure ? _strings.GetString("UpdateUpToDate") : string.Empty;
                    break;
            }
        }
        catch (Exception ex)
        {
            // Only the class is traced: a message can carry a URL.
            System.Diagnostics.Trace.TraceWarning($"update check failed: {ex.GetType().Name}");
            StatusText = reportFailure ? _strings.GetString("UpdateCheckFailed") : string.Empty;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Offer(AvailableUpdate update)
    {
        _downloaded = false;
        AvailableVersion = update.Version;
        ReleaseNotes = update.ReleaseNotes ?? string.Empty;
        BannerTitle = string.Format(System.Globalization.CultureInfo.CurrentCulture, _strings.GetString("UpdateAvailable_Title"), update.Version);
        StatusText = string.Format(System.Globalization.CultureInfo.CurrentCulture, _strings.GetString("UpdateAvailable_Status"), update.Version);
        HasUpdate = true;
        IsBannerOpen = true;
    }

    private void ClearOffer()
    {
        _downloaded = false;
        HasUpdate = false;
        IsBannerOpen = false;
        AvailableVersion = string.Empty;
        ReleaseNotes = string.Empty;
        BannerTitle = string.Empty;
    }

    /// <summary>Downloads (once) and restarts into the new version. Refuses while a run is active, before and again after the download, because the download can take minutes.</summary>
    [RelayCommand(CanExecute = nameof(CanRestart))]
    private async Task RestartToUpdateAsync()
    {
        if (_runs.AnyActive)
        {
            StatusText = _strings.GetString("UpdateWaitForRun");
            return;
        }

        IsBusy = true;
        try
        {
            if (!_downloaded)
            {
                StatusText = _strings.GetString("UpdateDownloading");
                await _source.DownloadAsync(percent => StatusText = string.Format(System.Globalization.CultureInfo.CurrentCulture, _strings.GetString("UpdateDownloadingPercent"), percent), CancellationToken.None).ConfigureAwait(true);
                _downloaded = true;
            }

            if (_runs.AnyActive)
            {
                StatusText = _strings.GetString("UpdateWaitForRun");
                return;
            }

            StatusText = _strings.GetString("UpdateRestarting");
            _source.ApplyAndRestart();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"update download failed: {ex.GetType().Name}");
            _downloaded = false;
            StatusText = _strings.GetString("UpdateDownloadFailed");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanRestart() => HasUpdate && !IsBusy;

    partial void OnChannelIndexChanged(int value)
    {
        if (_loading)
        {
            return;
        }

        // An update offered on the other channel is not an offer on this one.
        ClearOffer();
        StatusText = string.Empty;
        Save(() => _settings.Channel = value == 1 ? UpdateChannel.Beta : UpdateChannel.Stable);
    }

    partial void OnCheckOnLaunchChanged(bool value)
    {
        if (!_loading)
        {
            Save(() => _settings.CheckOnLaunch = value);
        }
    }

    private void Save(Action write)
    {
        try
        {
            write();
        }
        catch (SettingsUnavailableException ex)
        {
            System.Diagnostics.Trace.TraceWarning($"settings_unavailable while saving an update setting: {ex.GetType().Name}");
            StatusText = _strings.GetString("UpdateSettingNotSaved");
        }
    }

    private void Reload()
    {
        _loading = true;
        try
        {
            ChannelIndex = _settings.Channel == UpdateChannel.Beta ? 1 : 0;
            CheckOnLaunch = _settings.CheckOnLaunch;
            ClearOffer();
            StatusText = string.Empty;
        }
        finally
        {
            _loading = false;
        }
    }
}
