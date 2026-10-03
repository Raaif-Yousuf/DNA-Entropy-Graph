using CommunityToolkit.Mvvm.ComponentModel;
using DnaEntropyGraph.Core.Abstractions;

namespace DnaEntropyGraph.Presentation.ViewModels;

/// <summary>
/// The shell's in-app message state (issue #585) and the real <see cref="IToastService"/>. One message at a time: a new
/// one replaces the old. Info and success close after <see cref="AutoDismissAfter"/>; warning and error stay until
/// the user closes the bar (the InfoBar writes <see cref="IsOpen"/> back through its two-way binding). State changes and the timer callback both go through the
/// <see cref="IDispatcher"/>, so a caller on any thread is safe, and the timer runs on the injected <see cref="TimeProvider"/>
/// so tests never sleep. Singleton: <see cref="ShellViewModel"/> exposes it as <c>Messages</c> to the InfoBar.
/// </summary>
public sealed partial class InAppMessageCenter : ObservableObject, IToastService
{
    public static readonly TimeSpan AutoDismissAfter = TimeSpan.FromSeconds(6);

    private readonly IDispatcher _dispatcher;
    private readonly TimeProvider _time;
    private ITimer? _timer;
    private int _generation;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _message = string.Empty;

    [ObservableProperty]
    private ToastSeverity _severity;

    [ObservableProperty]
    private bool _isOpen;

    public InAppMessageCenter(IDispatcher dispatcher, TimeProvider time)
    {
        _dispatcher = dispatcher;
        _time = time;
    }

    public void ShowToast(string title, string body, ToastSeverity severity = ToastSeverity.Info)
        => _dispatcher.Enqueue(() => Show(title, body, severity));

    private void Show(string title, string body, ToastSeverity severity)
    {
        _timer?.Dispose();
        _timer = null;
        var generation = ++_generation;

        Title = title;
        Message = body;
        Severity = severity;
        IsOpen = true;

        if (severity is ToastSeverity.Info or ToastSeverity.Success)
        {
            _timer = _time.CreateTimer(
                _ => _dispatcher.Enqueue(() =>
                {
                    if (generation == _generation)
                    {
                        IsOpen = false;
                    }
                }),
                null,
                AutoDismissAfter,
                Timeout.InfiniteTimeSpan);
        }
    }
}
