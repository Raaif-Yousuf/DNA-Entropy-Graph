namespace DnaEntropyGraph.Core.Abstractions;

/// <summary>Wraps the WinUI <c>Frame</c> navigation so Presentation never touches WinUI types.</summary>
public interface INavigator
{
    void NavigateTo(string pageKey, object? parameter = null);

    bool CanGoBack { get; }

    void GoBack();

    /// <summary>The key of the page that is really showing (after a nav click, a deep link or Back); null before the first navigation (issue #143).</summary>
    string? CurrentPageKey { get; }

    /// <summary>Raised after <see cref="CurrentPageKey"/> changed, on whatever path got there.</summary>
    event EventHandler? CurrentPageChanged;
}
