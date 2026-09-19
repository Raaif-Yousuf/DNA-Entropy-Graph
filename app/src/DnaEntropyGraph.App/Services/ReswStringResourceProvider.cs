using System;
using DnaEntropyGraph.Presentation.Services;
using Microsoft.Windows.ApplicationModel.Resources;

namespace DnaEntropyGraph.App.Services;

/// <summary>
/// Reads <c>Strings/en-US/Resources.resw</c> through the Windows App SDK's
/// <see cref="ResourceLoader"/> (Hard Rule 13). The parameterless
/// <see cref="ResourceLoader"/> constructor targets the "Resources" subtree
/// of the app's main resource map by convention, which is exactly the
/// map an SDK-default <c>Resources.resw</c> under <c>Strings/en-US/</c>
/// produces.
///
/// THEORY (unverified): whether this actually resolves a key at runtime has
/// never been observed - nobody has launched this app yet, deliberately,
/// per this session's brief (CLAUDE.md's Critical Pitfalls: a .resw not
/// packed into a real PRI resource file gives a window whose every label is
/// blank, and an unpackaged WinUI 3 app resolves the Windows App SDK
/// bootstrapper itself at runtime, so a failure here could as easily be
/// that as a resource-map miss). See docs/ToTest.md.
///
/// Falls back to the key itself rather than throwing or returning an empty
/// string: a PRI packaging failure then shows a visible, greppable resource
/// key on screen ("PhaseRunning_Title") instead of a blank label that reads
/// as an unfinished layout with no clue why.
/// </summary>
public sealed class ReswStringResourceProvider : IStringResourceProvider
{
    private readonly ResourceLoader _resourceLoader = new();

    public string GetString(string key)
    {
        try
        {
            var value = _resourceLoader.GetString(key);
            return string.IsNullOrEmpty(value) ? key : value;
        }
        catch (Exception)
        {
            return key;
        }
    }
}
