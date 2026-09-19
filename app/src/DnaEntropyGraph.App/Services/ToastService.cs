using System.Diagnostics;
using DnaEntropyGraph.Core.Abstractions;

namespace DnaEntropyGraph.App.Services;

/// <summary>
/// Placeholder for Windows App Notifications. Real toast wiring (AUMID
/// registration, <c>Microsoft.Windows.AppNotifications</c>) is a follow-up
/// issue; this logs instead of silently discarding, so a missed
/// notification is still visible somewhere while the real UI does not
/// exist yet.
/// </summary>
public sealed class ToastService : IToastService
{
    public void ShowToast(string title, string body) => Debug.WriteLine($"[toast] {title}: {body}");
}
