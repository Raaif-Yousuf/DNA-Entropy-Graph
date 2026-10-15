namespace DnaEntropyGraph.Presentation.Services;

/// <summary>
/// Whether the shell has done its first navigation (issue #108). The window selects New run when its navigation view loads, which is
/// after App.OnLaunched; an Open-with launch handled earlier would navigate and then be overwritten by that first navigation.
/// <see cref="ActivationRouter"/> hands its work to <see cref="WhenReady"/>, which runs it at once once <see cref="MarkReady"/> has
/// been called (on the UI thread, after the first navigation) and holds it until then.
/// </summary>
public sealed class ShellReadiness
{
    private readonly object _gate = new();
    private readonly List<Action> _pending = [];
    private bool _ready;

    public bool IsReady
    {
        get
        {
            lock (_gate)
            {
                return _ready;
            }
        }
    }

    /// <summary>Runs <paramref name="work"/> now when the shell is ready, otherwise when it becomes ready. Any thread.</summary>
    public void WhenReady(Action work)
    {
        lock (_gate)
        {
            if (!_ready)
            {
                _pending.Add(work);
                return;
            }
        }

        Run(work);
    }

    public void MarkReady()
    {
        Action[] queued;
        lock (_gate)
        {
            if (_ready)
            {
                return;
            }

            _ready = true;
            queued = [.. _pending];
            _pending.Clear();
        }

        foreach (var work in queued)
        {
            Run(work);
        }
    }

    private static void Run(Action work)
    {
        try
        {
            work();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // One activation that cannot be handled must not drop the ones behind it.
            System.Diagnostics.Trace.TraceWarning($"queued activation failed: {ex.GetType().Name}");
        }
    }
}
