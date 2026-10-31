using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Runs;

namespace DnaEntropyGraph.Presentation.Services;

/// <summary>The answer to "may this be the output folder": accepted, or refused with the plain text to show beside the picker.</summary>
public readonly record struct OutputFolderVerdict(bool Accepted, string? Message);

/// <summary>
/// The rule for a folder the user picks as the place results are saved (issue #954). DECISION (agent-made, reversible): a whole drive (<c>D:\</c>) is refused outright rather
/// than accepted with a warning, so a run never lands where the History can neither delete nor re-download it (<see cref="RunOutputRoot.IsSafelyInside"/> refuses a drive root as the
/// containing folder; Hard Rule 14). A network share root (<c>\\server\share</c>) is a normal output folder and is accepted. Any picker for the output folder (Settings, New run)
/// calls <see cref="Check"/> and shows <see cref="OutputFolderVerdict.Message"/> inline when it is refused.
/// </summary>
public static class OutputFolderChoice
{
    /// <summary>The <c>Resources.resw</c> key of the refusal: says why in plain words and names the one action (pick a folder inside the drive).</summary>
    public const string DriveRootMessage = "OutputFolder_DriveRoot_Message";

    public static OutputFolderVerdict Check(string? path, IStringResourceProvider strings)
    {
        ArgumentNullException.ThrowIfNull(strings);

        // No folder means the default (Downloads), which is never a drive root.
        if (string.IsNullOrWhiteSpace(path))
        {
            return new(true, null);
        }

        return IsRefused(path) ? new(false, strings.GetString(DriveRootMessage)) : new(true, null);
    }

    /// <summary>True when <paramref name="path"/> is a whole drive. A path that cannot be resolved is not refused here: it is the run's own failure to report (output_folder_unusable).</summary>
    public static bool IsRefused(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            return RunOutputRoot.IsDriveRoot(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
