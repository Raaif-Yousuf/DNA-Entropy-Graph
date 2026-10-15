using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.LocalEngine;
using DnaEntropyGraph.Presentation.Services;

namespace DnaEntropyGraph.Presentation.ViewModels;

/// <summary>
/// The Settings "This PC" group (issues #120, #172, #173): the health card (graphics card, memory, driver, disk, and whether the
/// local engine answers), the one-click Install, Repair and Remove, and the default place to run (<see cref="Picker"/>).
/// Every sentence comes from Resources.resw; a failure shows the text of its code, which names one action (Hard Rule 13).
/// </summary>
public sealed partial class ThisPcSettingsViewModel : ObservableObject
{
    private const double BytesPerGb = 1024d * 1024d * 1024d;

    private readonly ILocalReadinessSource _readiness;
    private readonly ILocalEngineInstaller _installer;
    private readonly IDialogService _dialogs;
    private readonly IStringResourceProvider _strings;
    private CancellationTokenSource? _installCancel;

    [ObservableProperty]
    private string _gpuText = string.Empty;

    [ObservableProperty]
    private string _diskText = string.Empty;

    [ObservableProperty]
    private string _engineText = string.Empty;

    /// <summary>What stops this PC from running a model, one sentence per problem; empty when nothing does.</summary>
    [ObservableProperty]
    private string _blockersText = string.Empty;

    /// <summary>True once the first check has finished.</summary>
    [ObservableProperty]
    private bool _isChecked;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand), nameof(RepairCommand), nameof(RemoveCommand), nameof(RefreshCommand), nameof(CancelInstallCommand))]
    private bool _isBusy;

    /// <summary>What the last check found; decides which of Install, Repair and Remove are offered.</summary>
    public LocalEngineState EngineState { get; private set; } = LocalEngineState.NotInstalled;

    /// <summary>The step the install is on, and the last line of what it is doing.</summary>
    [ObservableProperty]
    private string _progressText = string.Empty;

    [ObservableProperty]
    private string _statusText = string.Empty;

    public ThisPcSettingsViewModel(
        ILocalReadinessSource readiness,
        ILocalEngineInstaller installer,
        IDialogService dialogs,
        IStringResourceProvider strings,
        RunTargetPickerViewModel picker)
    {
        _readiness = readiness;
        _installer = installer;
        _dialogs = dialogs;
        _strings = strings;
        Picker = picker;
    }

    public RunTargetPickerViewModel Picker { get; }

    /// <summary>Looks at this PC and the engine again and fills the health card. Never throws.</summary>
    [RelayCommand(CanExecute = nameof(CanWork))]
    public async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            await CheckAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanWork() => !IsBusy;

    private bool CanInstall() => !IsBusy && EngineState == LocalEngineState.NotInstalled;

    private bool CanRepairOrRemove() => !IsBusy && EngineState != LocalEngineState.NotInstalled;

    [RelayCommand(CanExecute = nameof(CanInstall))]
    public Task InstallAsync() => RunInstallerAsync(static (installer, progress, token) => installer.InstallAsync(progress, token));

    [RelayCommand(CanExecute = nameof(CanRepairOrRemove))]
    public Task RepairAsync() => RunInstallerAsync(static (installer, progress, token) => installer.RepairAsync(progress, token));

    [RelayCommand(CanExecute = nameof(CanRepairOrRemove))]
    public async Task RemoveAsync()
    {
        var confirmed = await _dialogs.ConfirmAsync(
            _strings.GetString("ThisPcRemoveConfirmTitle"),
            _strings.GetString("ThisPcRemoveConfirmMessage"),
            CancellationToken.None).ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }

        IsBusy = true;
        try
        {
            ProgressText = _strings.GetString("ThisPcStep_Removing");
            var result = await _installer.RemoveAsync(CancellationToken.None).ConfigureAwait(true);
            StatusText = result.Succeeded ? _strings.GetString("ThisPcRemoved") : _strings.GetString(LocalEngineErrorCodes.ResourceKey(result.ErrorCode));
            ProgressText = string.Empty;
            await CheckAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(IsBusy))]
    public void CancelInstall() => _installCancel?.Cancel();

    private async Task RunInstallerAsync(Func<ILocalEngineInstaller, IProgress<LocalInstallProgress>, CancellationToken, Task<LocalInstallResult>> action)
    {
        IsBusy = true;
        StatusText = string.Empty;
        _installCancel = new CancellationTokenSource();
        try
        {
            // Created here, on the thread that pressed the button, so every report is shown on that thread.
            // A report that arrives after the install has ended (Progress posts it) must not bring a step line back.
            var progress = new Progress<LocalInstallProgress>(p =>
            {
                if (IsBusy)
                {
                    ProgressText = _strings.GetString(StepKey(p.Step)) + (string.IsNullOrEmpty(p.Line) ? string.Empty : " " + p.Line);
                }
            });
            var result = await action(_installer, progress, _installCancel.Token).ConfigureAwait(true);
            StatusText = result.Succeeded ? _strings.GetString("ThisPcInstalled") : _strings.GetString(LocalEngineErrorCodes.ResourceKey(result.ErrorCode));
            ProgressText = string.Empty;
            await CheckAsync().ConfigureAwait(true);
        }
        finally
        {
            _installCancel.Dispose();
            _installCancel = null;
            IsBusy = false;
        }
    }

    private static string StepKey(LocalInstallStep step) => step switch
    {
        LocalInstallStep.CreatingEnvironment => "ThisPcStep_CreatingEnvironment",
        LocalInstallStep.InstallingPackages => "ThisPcStep_InstallingPackages",
        LocalInstallStep.InstallingWorker => "ThisPcStep_InstallingWorker",
        LocalInstallStep.Checking => "ThisPcStep_Checking",
        _ => "ThisPcStep_Removing",
    };

    private static string EngineKey(LocalEngineState state) => state switch
    {
        LocalEngineState.NotInstalled => "ThisPcEngine_NotInstalled",
        LocalEngineState.Broken => "ThisPcEngine_Broken",
        LocalEngineState.WorkerVersionMismatch => "ThisPcEngine_WorkerVersionMismatch",
        _ => "ThisPcEngine_Healthy",
    };

    private static string BlockerKey(LocalBlocker blocker) => blocker switch
    {
        LocalBlocker.NoNvidiaGpu => "ThisPcBlocker_NoNvidiaGpu",
        LocalBlocker.GpuToolFailed => "ThisPcBlocker_GpuToolFailed",
        LocalBlocker.NotEnoughGpuMemory => "ThisPcBlocker_NotEnoughGpuMemory",
        LocalBlocker.DriverTooOld => "ThisPcBlocker_DriverTooOld",
        _ => "ThisPcBlocker_NotEnoughDisk",
    };

    private async Task CheckAsync()
    {
        LocalReadiness readiness;
        try
        {
            readiness = await _readiness.CheckAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            EngineText = _strings.GetString("ThisPcCheckFailed");
            IsChecked = true;
            return;
        }

        var capability = readiness.Capability;
        GpuText = capability.Gpu is null
            ? _strings.GetString("ThisPcNoGpu")
            : string.Format(
                CultureInfo.CurrentCulture,
                _strings.GetString("ThisPcGpuFormat"),
                capability.Gpu.Name,
                (capability.Gpu.MemoryMiB / 1024d).ToString("0", CultureInfo.CurrentCulture),
                capability.Gpu.DriverVersion);
        DiskText = string.Format(CultureInfo.CurrentCulture, _strings.GetString("ThisPcDiskFormat"), (capability.FreeDiskBytes / BytesPerGb).ToString("0", CultureInfo.CurrentCulture));
        EngineState = readiness.Engine.State;
        InstallCommand.NotifyCanExecuteChanged();
        RepairCommand.NotifyCanExecuteChanged();
        RemoveCommand.NotifyCanExecuteChanged();
        EngineText = _strings.GetString(EngineKey(readiness.Engine.State))
            + (readiness.Engine.State == LocalEngineState.Healthy && !readiness.Engine.CudaAvailable ? " " + _strings.GetString("ThisPcEngineNoCuda") : string.Empty);
        BlockersText = string.Join(' ', capability.Blockers.Select(b => _strings.GetString(BlockerKey(b))));
        IsChecked = true;
        await Picker.RefreshAsync().ConfigureAwait(true);
    }
}
