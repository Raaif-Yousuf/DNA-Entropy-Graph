namespace DnaEntropyGraph.Core.Abstractions;

/// <summary>Wraps the WinUI <c>Frame</c> navigation so Presentation never touches WinUI types.</summary>
public interface INavigator
{
    void NavigateTo(string pageKey, object? parameter = null);

    bool CanGoBack { get; }

    void GoBack();
}
