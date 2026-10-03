namespace DnaEntropyGraph.Presentation.Services;

/// <summary>
/// What the Results page asks Windows to do with a result file or folder (issue #102). Abstracted so a ViewModel
/// stays free of WinUI and Process types (Hard Rule 8). Each call returns false when it could not do it (the file or folder is
/// gone, no program is registered for the file type, the clipboard is busy), so the page can say so and name an action.
/// Read-only with respect to the user's files (Hard Rule 14): none of these change a file.
/// </summary>
public interface IShellLauncher
{
    /// <summary>Opens <paramref name="path"/> in the program Windows has registered for its type.</summary>
    bool OpenFile(string path);

    /// <summary>Opens File Explorer with <paramref name="path"/> selected.</summary>
    bool ShowInFolder(string path);

    /// <summary>Opens File Explorer on <paramref name="folder"/>.</summary>
    bool OpenFolder(string folder);

    /// <summary>Puts <paramref name="text"/> on the clipboard.</summary>
    bool CopyText(string text);
}
