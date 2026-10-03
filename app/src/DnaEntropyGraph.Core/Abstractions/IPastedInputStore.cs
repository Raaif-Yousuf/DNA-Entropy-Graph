namespace DnaEntropyGraph.Core.Abstractions;

/// <summary>Where a pasted sequence goes so it can be validated and run like a file (issue #63).</summary>
public interface IPastedInputStore
{
    /// <summary>Saves <paramref name="text"/> as a new file under the app's own data folder and returns its path. Never writes next to a user's file (Hard Rule 14).</summary>
    string Save(string text);
}
