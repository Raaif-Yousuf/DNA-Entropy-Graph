namespace DnaEntropyGraph.Core;

/// <summary>
/// The page keys behind the navigation frame's back stack (issue #143): one key per page entry, so the shell can name the page that is
/// really showing after a deep link or Back. Pure, so it is tested without a frame.
/// </summary>
public sealed class PageKeyHistory
{
    private readonly List<string> _keys = [];

    public string? Current => _keys.Count == 0 ? null : _keys[^1];

    public void Push(string key) => _keys.Add(key);

    /// <summary>Drops the current key and returns the one now current (the last one is never dropped).</summary>
    public string? Pop()
    {
        if (_keys.Count > 1)
        {
            _keys.RemoveAt(_keys.Count - 1);
        }

        return Current;
    }
}
