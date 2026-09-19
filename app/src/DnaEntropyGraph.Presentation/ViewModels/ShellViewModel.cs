using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DnaEntropyGraph.Core.Abstractions;

namespace DnaEntropyGraph.Presentation.ViewModels;

/// <summary>The app shell: nav rail + current page host (docs/superpowers/specs Appendix A section 2).</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly INavigator _navigator;

    [ObservableProperty]
    private string _currentPageKey = "NewRun";

    public ShellViewModel(INavigator navigator)
    {
        _navigator = navigator;
    }

    [RelayCommand]
    private void NavigateTo(string pageKey)
    {
        _navigator.NavigateTo(pageKey);
        CurrentPageKey = pageKey;
    }
}
