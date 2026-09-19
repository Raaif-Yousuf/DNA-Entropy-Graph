using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Presentation.Services;

namespace DnaEntropyGraph.Presentation.ViewModels;

/// <summary>SettingsCards: theme, default output folder, accounts, diagnostics zip (docs/superpowers/specs Appendix A section 2.8).</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private const string ThemeKey = "Theme";

    private readonly ISettingsStore _settingsStore;
    private readonly IToastService _toastService;
    private readonly IStringResourceProvider _strings;

    [ObservableProperty]
    private string _theme;

    public SettingsViewModel(ISettingsStore settingsStore, IToastService toastService, IStringResourceProvider strings)
    {
        _settingsStore = settingsStore;
        _toastService = toastService;
        _strings = strings;
        _theme = settingsStore.GetString(ThemeKey) ?? "System";
    }

    [RelayCommand]
    private void SetTheme(string theme)
    {
        Theme = theme;
        _settingsStore.SetString(ThemeKey, theme);
        _toastService.ShowToast(_strings.GetString("ThemeUpdated.Title"), theme);
    }
}
