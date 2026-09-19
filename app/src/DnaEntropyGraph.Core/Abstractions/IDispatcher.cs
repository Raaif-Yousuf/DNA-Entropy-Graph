namespace DnaEntropyGraph.Core.Abstractions;

/// <summary>
/// Marshals a callback onto the UI thread. Presentation injects this
/// instead of calling <c>DispatcherQueue.GetForCurrentThread()</c> directly,
/// so ViewModel tests run with no UI thread at all (Hard Rule 8, and the
/// winui-dev skill's Threading section).
/// </summary>
public interface IDispatcher
{
    void Enqueue(Action action);
}
