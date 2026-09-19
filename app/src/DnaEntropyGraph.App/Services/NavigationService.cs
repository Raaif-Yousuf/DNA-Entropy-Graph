using DnaEntropyGraph.Core.Abstractions;
using Microsoft.UI.Xaml.Controls;

namespace DnaEntropyGraph.App.Services;

/// <summary>
/// Wraps the shell's <see cref="Frame"/>. <see cref="Initialize"/> is called
/// from <c>MainWindow</c> once the real Frame exists, never from this
/// class's constructor - so DI can resolve this service with no UI thread
/// (Guards.Tests). The page-key -> Page-type map is empty until the issue
/// that builds the actual Views registers real pages; until then,
/// <see cref="NavigateTo"/> is a documented no-op rather than a crash, so it
/// fails safe instead of pretending navigation is live.
/// </summary>
public sealed class NavigationService : INavigator
{
    private readonly Dictionary<string, Type> _pages = new(StringComparer.OrdinalIgnoreCase);
    private Frame? _frame;

    public void Initialize(Frame frame) => _frame = frame;

    public void RegisterPage(string pageKey, Type pageType) => _pages[pageKey] = pageType;

    public bool CanGoBack => _frame?.CanGoBack ?? false;

    public void NavigateTo(string pageKey, object? parameter = null)
    {
        if (_frame is null || !_pages.TryGetValue(pageKey, out var pageType))
        {
            return;
        }

        _frame.Navigate(pageType, parameter);
    }

    public void GoBack()
    {
        if (_frame?.CanGoBack == true)
        {
            _frame.GoBack();
        }
    }
}
