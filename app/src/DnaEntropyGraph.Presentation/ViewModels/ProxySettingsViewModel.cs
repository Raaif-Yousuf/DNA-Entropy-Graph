using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Net;
using DnaEntropyGraph.Presentation.Messaging;
using DnaEntropyGraph.Presentation.Services;

namespace DnaEntropyGraph.Presentation.ViewModels;

/// <summary>
/// The Settings "Advanced" network proxy section (issue #239): follow Windows (the default; covers a PAC script and a proxy that wants the
/// Windows sign-in), one typed proxy, or none. Apply saves the choice and the very next request of every client uses it
/// (<see cref="ProxyPolicy"/>). The proxy password is typed into a box that is cleared after Apply; it is stored only in the encrypted
/// credential file, never in settings.json (Hard Rule 12).
/// </summary>
public sealed partial class ProxySettingsViewModel : ObservableObject
{
    private readonly ProxyPolicy _policy;
    private readonly IStringResourceProvider _strings;

    /// <summary>0 = follow Windows, 1 = a proxy I type, 2 = no proxy. The same order as the three items in the page's ComboBox.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsManual))]
    private int _selectedModeIndex;

    [ObservableProperty]
    private string _manualAddress = string.Empty;

    [ObservableProperty]
    private string _proxyUserName = string.Empty;

    [ObservableProperty]
    private string _proxyPassword = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string _status = string.Empty;

    [ObservableProperty]
    private bool _hasSavedSignIn;

    public ProxySettingsViewModel(ProxyPolicy policy, IStringResourceProvider strings, IMessenger? messenger = null)
    {
        _policy = policy;
        _strings = strings;
        Load();
        messenger?.Register<ProxySettingsViewModel, SettingsResetMessage>(this, static (vm, _) => vm.Reset());
    }

    public bool IsManual => SelectedModeIndex == 1;

    public bool HasStatus => Status.Length > 0;

    private void Load()
    {
        var settings = _policy.Settings;
        SelectedModeIndex = settings.Mode switch { ProxyMode.Manual => 1, ProxyMode.None => 2, _ => 0 };
        ManualAddress = settings.ManualAddress;
        ProxyUserName = _policy.ManualCredential?.UserName ?? string.Empty;
        ProxyPassword = string.Empty;
        HasSavedSignIn = _policy.ManualCredential is not null;
        Status = string.Empty;
    }

    private void Reset()
    {
        _policy.ResetToDefault();
        Load();
    }

    [RelayCommand]
    private void Apply()
    {
        var mode = SelectedModeIndex switch { 1 => ProxyMode.Manual, 2 => ProxyMode.None, _ => ProxyMode.System };
        var address = ManualAddress.Trim();
        NetworkCredential? credential = null;
        if (mode == ProxyMode.Manual)
        {
            if (!ProxyAddress.TryParse(address, out _))
            {
                Status = _strings.GetString("ProxyAddressInvalid");
                return;
            }

            var user = ProxyUserName.Trim();
            if (user.Length > 0)
            {
                var saved = _policy.ManualCredential;
                var password = ProxyPassword.Length > 0 ? ProxyPassword : saved is not null && saved.UserName == user ? saved.Password : string.Empty;
                if (password.Length == 0)
                {
                    Status = _strings.GetString("ProxyPasswordNeeded");
                    return;
                }

                credential = new NetworkCredential(user, password);
            }
        }

        try
        {
            _policy.Apply(new ProxySettings(mode, mode == ProxyMode.Manual ? address : string.Empty), credential);
        }
        catch (Exception ex) when (ex is SettingsUnavailableException or ProxyCredentialUnreadableException)
        {
            System.Diagnostics.Trace.TraceWarning($"proxy settings not saved: {ex.GetType().Name}");
            Status = _strings.GetString("AdvancedNotSaved");
            return;
        }

        ProxyPassword = string.Empty;
        HasSavedSignIn = credential is not null;
        if (mode != ProxyMode.Manual)
        {
            ProxyUserName = string.Empty;
        }

        Status = _strings.GetString("ProxySaved");
    }
}
