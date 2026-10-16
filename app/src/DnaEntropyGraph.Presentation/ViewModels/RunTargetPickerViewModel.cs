using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.LocalEngine;
using DnaEntropyGraph.Presentation.Messaging;
using DnaEntropyGraph.Presentation.Services;

namespace DnaEntropyGraph.Presentation.ViewModels;

/// <summary>One choice in the "Where to run" picker. A disabled choice says why in <see cref="Reason"/>, so it is greyed out with a reason, never hidden.</summary>
public sealed partial class RunTargetOption : ObservableObject
{
    public RunTargetOption(string value, string label)
    {
        Value = value;
        Label = label;
    }

    /// <summary>One of <see cref="RunTargets"/>: what <c>RunOptions.RunTarget</c> holds.</summary>
    public string Value { get; }

    public string Label { get; }

    [ObservableProperty]
    private bool _isAvailable = true;

    /// <summary>Why this choice is off (read by tests and the note line); the page shows the This PC reason through <see cref="RunTargetPickerViewModel.ThisPcReason"/>.</summary>
    public string Reason { get; set; } = string.Empty;
}

/// <summary>
/// The Cloud / This PC / Auto choice (issues #120 and #173), shared by Settings (the default) and New run. The choice is saved under
/// <see cref="SettingKey"/>, which <see cref="Read"/> turns back into a target for a new run. This PC is greyed out, with the reason,
/// while this PC cannot run the model (no NVIDIA card, too little graphics memory, an old driver, too little disk, or the engine is not
/// installed); Auto and Cloud are always available, because Auto falls back to the cloud by itself.
/// </summary>
public sealed partial class RunTargetPickerViewModel : ObservableObject
{
    public const string SettingKey = "run.target";

    private readonly ISettingsStore _settings;
    private readonly ILocalReadinessSource _readiness;
    private readonly IStringResourceProvider _strings;
    private bool _loading;

    [ObservableProperty]
    private string _selectedTarget = RunTargets.Cloud;

    /// <summary>The line under the picker: why This PC is unavailable, or what Auto will do. Empty when there is nothing to add.</summary>
    [ObservableProperty]
    private string _note = string.Empty;

    /// <summary>Why This PC is greyed out, shown whichever choice is selected; empty when This PC is available.</summary>
    [ObservableProperty]
    private string _thisPcReason = string.Empty;

    [ObservableProperty]
    private string _saveStatus = string.Empty;

    public RunTargetPickerViewModel(ISettingsStore settings, ILocalReadinessSource readiness, IStringResourceProvider strings, IMessenger? messenger = null)
    {
        _settings = settings;
        _readiness = readiness;
        _strings = strings;
        Options =
        [
            new RunTargetOption(RunTargets.Cloud, strings.GetString("RunTarget_Cloud")),
            new RunTargetOption(RunTargets.ThisPc, strings.GetString("RunTarget_ThisPc")),
            new RunTargetOption(RunTargets.Auto, strings.GetString("RunTarget_Auto")),
        ];
        // A field, not the property: opening a page must not save what was just read.
        _selectedTarget = Read(settings);
        _acceptedTarget = _selectedTarget;
        messenger?.Register<RunTargetPickerViewModel, SettingsResetMessage>(this, static (vm, _) => vm.Reload());
    }

    /// <summary>Re-reads the saved choice after Reset all settings, without saving what was just read.</summary>
    private void Reload()
    {
        _loading = true;
        try
        {
            SelectedTarget = Read(_settings);
            SaveStatus = string.Empty;
        }
        finally
        {
            _loading = false;
        }
    }

    public ObservableCollection<RunTargetOption> Options { get; }

    /// <summary>The saved choice, or Cloud when none is saved, the file cannot be read or the value is not one the picker offers.</summary>
    public static string Read(ISettingsStore settings)
    {
        string? saved;
        try
        {
            saved = settings.GetString(SettingKey);
        }
        catch (SettingsUnavailableException)
        {
            return RunTargets.Cloud;
        }

        return saved switch
        {
            RunTargets.ThisPc => RunTargets.ThisPc,
            RunTargets.Auto => RunTargets.Auto,
            _ => RunTargets.Cloud,
        };
    }

    private string _acceptedTarget = RunTargets.Cloud;
    private bool _reverting;

    partial void OnSelectedTargetChanged(string value)
    {
        if (_reverting)
        {
            return;
        }

        if (_loading)
        {
            _acceptedTarget = value;
            UpdateNote(_lastReadiness);
            return;
        }

        // A choice the user cannot use is refused here, not only greyed out: the list greys the text, but its row can still be clicked.
        var thisPc = Options.First(o => o.Value == RunTargets.ThisPc);
        if (value == RunTargets.ThisPc && !thisPc.IsAvailable)
        {
            _reverting = true;
            try
            {
                SelectedTarget = _acceptedTarget;
            }
            finally
            {
                _reverting = false;
            }

            Note = thisPc.Reason;
            return;
        }

        _acceptedTarget = value;

        try
        {
            _settings.SetString(SettingKey, value);
            SaveStatus = string.Empty;
        }
        catch (SettingsUnavailableException)
        {
            SaveStatus = _strings.GetString("RunTargetNotSaved");
        }

        UpdateNote(_lastReadiness);
    }

    private LocalReadiness? _lastReadiness;

    /// <summary>Looks at this PC again and enables or greys out This PC. Never throws.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        LocalReadiness? readiness;
        try
        {
            // No ConfigureAwait(false): the code below sets properties the page binds, so it must resume on the caller's (UI) context.
            readiness = await _readiness.CheckAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            readiness = null;
        }

        _lastReadiness = readiness;
        var thisPc = Options.First(o => o.Value == RunTargets.ThisPc);
        var reason = readiness is null ? _strings.GetString("RunTargetReason_unknown") : ReasonFor(readiness);
        thisPc.IsAvailable = reason.Length == 0;
        thisPc.Reason = reason;
        ThisPcReason = reason;
        UpdateNote(readiness);
    }

    private string ReasonFor(LocalReadiness readiness)
    {
        // The hardware reasons first: they are what the user can act on first, and an installed engine on a PC with no NVIDIA card is not the fix.
        if (readiness.Capability.Blockers.Count > 0)
        {
            return _strings.GetString(BlockerReasonKey(readiness.Capability.Blockers[0]));
        }

        return readiness.Engine.State switch
        {
            LocalEngineState.Healthy when readiness.Engine.CudaAvailable => string.Empty,
            LocalEngineState.Healthy => _strings.GetString("RunTargetReason_NoCuda"),
            LocalEngineState.NotInstalled => _strings.GetString("RunTargetReason_NotInstalled"),
            _ => _strings.GetString("RunTargetReason_EngineProblem"),
        };
    }

    private static string BlockerReasonKey(LocalBlocker blocker) => blocker switch
    {
        LocalBlocker.NoNvidiaGpu => "RunTargetReason_NoNvidiaGpu",
        LocalBlocker.GpuToolFailed => "RunTargetReason_GpuToolFailed",
        LocalBlocker.NotEnoughGpuMemory => "RunTargetReason_NotEnoughGpuMemory",
        LocalBlocker.DriverTooOld => "RunTargetReason_DriverTooOld",
        _ => "RunTargetReason_NotEnoughDisk",
    };

    private void UpdateNote(LocalReadiness? readiness)
    {
        var thisPc = Options.First(o => o.Value == RunTargets.ThisPc);
        Note = SelectedTarget switch
        {
            RunTargets.ThisPc when !thisPc.IsAvailable && thisPc.Reason.Length > 0 => thisPc.Reason,
            RunTargets.Auto when readiness is not null => _strings.GetString(thisPc.IsAvailable ? "RunTargetAutoLocal" : "RunTargetAutoCloud"),
            RunTargets.Auto => _strings.GetString("RunTargetAutoCloud"),
            _ => string.Empty,
        };
    }
}
