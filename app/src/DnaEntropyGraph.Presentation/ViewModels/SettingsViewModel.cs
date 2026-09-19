using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DnaEntropyGraph.Core.Abstractions;

namespace DnaEntropyGraph.Presentation.ViewModels;

/// <summary>SettingsCards: theme, default output folder, accounts, diagnostics zip (docs/superpowers/specs Appendix A section 2.8).</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private const string ThemeKey = "Theme";

    private readonly ISettingsStore _settingsStore;
    private readonly IToastService _toastService;

    [ObservableProperty]
    private string _theme;

    public SettingsViewModel(ISettingsStore settingsStore, IToastService toastService)
    {
        _settingsStore = settingsStore;
        _toastService = toastService;
        _theme = settingsStore.GetString(ThemeKey) ?? "System";
    }

    [RelayCommand]
    private void SetTheme(string theme)
    {
        Theme = theme;
        _settingsStore.SetString(ThemeKey, theme);
        _toastService.ShowToast("Theme updated", theme);
    }
}
