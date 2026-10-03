namespace DnaEntropyGraph.Core.Abstractions;

/// <summary>Where a pasted sequence goes so it can be validated and run like a file (issue #63).</summary>
public interface IPastedInputStore
{
    /// <summary>Saves <paramref name="text"/> as a new file under the app's own data folder and returns its path. Never writes next to a user's file (Hard Rule 14).</summary>
    string Save(string text);

    /// <summary>Deletes a file <see cref="Save"/> returned. Best effort: never throws for a file that is gone or locked, and does nothing for any path that is not one of our own saved pastes, so a user's file is never touched (Hard Rule 14).</summary>
    void Delete(string path);
}
