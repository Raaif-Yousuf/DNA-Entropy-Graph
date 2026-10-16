namespace DnaEntropyGraph.Presentation.ViewModels;

/// <summary>
/// What a page forwards from <c>OnNavigatedTo</c> (issue #527): the navigation parameter and whether this is a new navigation.
/// Back and Forward re-deliver the parameter the page was first opened with, which must not be acted on twice. Presentation has no
/// WinUI reference, so the page passes <c>NavigationMode</c> as its number (<see cref="NewNavigationMode"/>) and the decision is made here.
/// </summary>
public sealed record NavigationArrival(object? Parameter, bool IsNewNavigation)
{
    /// <summary><c>NavigationMode.New</c>.</summary>
    public const int NewNavigationMode = 0;

    public static NavigationArrival From(object? parameter, int navigationMode) => new(parameter, navigationMode == NewNavigationMode);
}
