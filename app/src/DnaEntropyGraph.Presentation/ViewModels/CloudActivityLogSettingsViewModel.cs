using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Presentation.Services;

namespace DnaEntropyGraph.Presentation.ViewModels;

/// <summary>
/// The Settings > Diagnostics "Open cloud activity log" button (issue #245): shows the audit file of every cloud change the app made
/// (create, start, stop, delete, bucket writes, IAM changes) selected in Explorer. It only reads; the same file is in the support zip.
/// </summary>
public sealed partial class CloudActivityLogSettingsViewModel(AppDataRoot root, IFolderLauncher launcher, IStringResourceProvider strings) : ObservableObject
{
    [ObservableProperty]
    private string _cloudActivityStatus = string.Empty;

    public string CloudActivityLogPath => root.CloudAuditLogFile;

    [RelayCommand]
    private void OpenCloudActivityLog()
    {
        var opened = false;
        try
        {
            if (!File.Exists(CloudActivityLogPath))
            {
                CloudActivityStatus = strings.GetString("CloudActivityLogNone");
                return;
            }

            opened = launcher.RevealFile(CloudActivityLogPath);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError($"Open cloud activity log failed: {ex.GetType().Name}");
        }

        CloudActivityStatus = opened ? string.Empty : strings.GetString("CloudActivityLogOpenFailed");
    }
}
