using CommunityToolkit.Mvvm.ComponentModel;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Presentation.Services;

namespace DnaEntropyGraph.Presentation.ViewModels;

/// <summary>
/// The Settings switch "Show DNA Entropy Graph in Open with for GenBank and FASTA files" (issue #108). It reads what is registered now
/// (nothing is written by opening the page) and adds or removes the entries through <see cref="FileAssociationService"/>. When Windows
/// refuses, the switch goes back to what is really registered and the line under it says what to do.
/// </summary>
public sealed partial class FileAssociationSettingsViewModel : ObservableObject
{
    private readonly FileAssociationService _service;
    private readonly IStringResourceProvider _strings;
    private bool _loading;

    [ObservableProperty]
    private bool _openWithEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOpenWithStatus))]
    private string _openWithStatus = string.Empty;

    public bool HasOpenWithStatus => OpenWithStatus.Length > 0;

    public FileAssociationSettingsViewModel(FileAssociationService service, IStringResourceProvider strings)
    {
        _service = service;
        _strings = strings;

        // The field, not the property: opening the page must not write what was just read.
        _openWithEnabled = SafeIsEnabled();
    }

    partial void OnOpenWithEnabledChanged(bool value)
    {
        if (_loading)
        {
            return;
        }

        try
        {
            if (value)
            {
                _service.Enable();
            }
            else
            {
                _service.Disable();
            }

            OpenWithStatus = string.Empty;
        }
        catch (FileAssociationRefusedException refused)
        {
            // Nothing was written: say why, by reason, and show what is really registered.
            System.Diagnostics.Trace.TraceInformation($"file association change refused: {refused.Reason}");
            SetBackToWhatIsRegistered(refused.Reason switch
            {
                FileAssociationRefusal.NotTheAppExecutable => "OpenWithNotInstalled",
                FileAssociationRefusal.SandboxProfile => "OpenWithSandbox",
                FileAssociationRefusal.PortableMode => "OpenWithPortable",
                _ => "OpenWithNotSaved",
            });
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or InvalidOperationException)
        {
            System.Diagnostics.Trace.TraceWarning($"file association could not be changed: {ex.GetType().Name}");
            SetBackToWhatIsRegistered("OpenWithNotSaved");
        }
    }

    private void SetBackToWhatIsRegistered(string statusKey)
    {
        _loading = true;
        try
        {
            OpenWithEnabled = SafeIsEnabled();
        }
        finally
        {
            _loading = false;
        }

        OpenWithStatus = _strings.GetString(statusKey);
    }

    private bool SafeIsEnabled()
    {
        try
        {
            return _service.IsEnabled;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return false;
        }
    }
}
