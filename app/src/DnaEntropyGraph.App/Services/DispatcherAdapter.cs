using DnaEntropyGraph.Core.Abstractions;
using Microsoft.UI.Dispatching;

namespace DnaEntropyGraph.App.Services;

/// <summary>
/// Marshals a callback onto the UI thread that constructed it. Captures
/// <see cref="DispatcherQueue.GetForCurrentThread"/> at construction time
/// rather than on every call, per the winui-dev skill's Threading section.
/// Falls back to a direct, synchronous invoke when there is no dispatcher
/// queue on the constructing thread (e.g. Guards.Tests resolving this from
/// a console test host with no UI thread at all) - this is what lets
/// Guards.Tests/DiResolutionTests resolve the real production DI graph
/// with no WinUI window ever created.
/// </summary>
public sealed class DispatcherAdapter : IDispatcher
{
    private readonly DispatcherQueue? _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

    public void Enqueue(Action action)
    {
        if (_dispatcherQueue is null)
        {
            action();
            return;
        }

        _dispatcherQueue.TryEnqueue(() => action());
    }
}
