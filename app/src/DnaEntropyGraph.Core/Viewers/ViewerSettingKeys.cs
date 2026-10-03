namespace DnaEntropyGraph.Core.Viewers;

/// <summary>
/// The <see cref="Abstractions.ISettingsStore"/> keys for the desktop viewers (issue #586), constants so the Settings page
/// (#104) can bind to the same names the Results page reads. Programs are full paths; an empty value means "not chosen".
/// </summary>
public static class ViewerSettingKeys
{
    public const string IgvPath = "Viewer.IgvPath";
    public const string GeneiousPath = "Viewer.GeneiousPath";

    /// <summary>IGV's batch port as a number; anything else means <see cref="IgvBatchCommands.DefaultPort"/>.</summary>
    public const string IgvPort = "Viewer.IgvPort";
}
