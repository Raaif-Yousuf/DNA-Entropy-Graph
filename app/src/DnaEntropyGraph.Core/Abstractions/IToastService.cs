namespace DnaEntropyGraph.Core.Abstractions;

/// <summary>Wraps Windows App Notifications so Presentation never touches WinUI types.</summary>
public interface IToastService
{
    void ShowToast(string title, string body);
}
