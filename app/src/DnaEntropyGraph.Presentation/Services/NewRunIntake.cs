using DnaEntropyGraph.Presentation.ViewModels;

namespace DnaEntropyGraph.Presentation.Services;

/// <summary>The New run screen as the intake sees it: somewhere files can be added.</summary>
public interface INewRunScreen
{
    void Add(DroppedItems items);
}

/// <summary>
/// Where files handed over by "Open with" (issue #108) go when a New run screen is already showing. Navigating builds a fresh page
/// with a fresh ViewModel, which would throw away a pasted sequence or a half-built list; adding to the live screen keeps it.
/// The page's ViewModel attaches when it is navigated to and detaches when it is left, so "a screen is attached" means "the user is
/// looking at New run".
/// </summary>
public sealed class NewRunIntake
{
    private readonly object _gate = new();
    private INewRunScreen? _current;

    public void Attach(INewRunScreen screen)
    {
        lock (_gate)
        {
            _current = screen;
        }
    }

    /// <summary>Detaches <paramref name="screen"/> only if it is still the current one (a newer page may have attached since).</summary>
    public void Detach(INewRunScreen screen)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_current, screen))
            {
                _current = null;
            }
        }
    }

    /// <summary>Adds the files to the showing screen. False when none is showing (the caller navigates instead).</summary>
    public bool TryAdd(DroppedItems items)
    {
        INewRunScreen? screen;
        lock (_gate)
        {
            screen = _current;
        }

        if (screen is null)
        {
            return false;
        }

        screen.Add(items);
        return true;
    }
}
