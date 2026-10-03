using System.Globalization;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Viewers;

namespace DnaEntropyGraph.Presentation.Services;

/// <summary>How an Open in IGV or Open in Geneious press ended; the Results page turns each failure into one message with one action.</summary>
public enum ExternalViewerOutcome
{
    Opened,

    /// <summary>The run has none of the files this viewer needs.</summary>
    NoFiles,

    /// <summary>No program was saved, found or chosen.</summary>
    NotFound,

    /// <summary>The program was found but would not start.</summary>
    LaunchFailed,

    /// <summary>IGV is running but refused a command.</summary>
    IgvRejected,

    /// <summary>IGV is running but did not answer in time.</summary>
    IgvNoReply,
}

/// <summary>
/// Open in IGV and Open in Geneious for the Results page (issue #586). IGV: if its batch port answers, drive it
/// (<c>new</c>, <c>genome</c>, <c>load</c>, <c>load</c>); if nothing listens, start it with the files. A running IGV that
/// misbehaves is reported, never answered with a second IGV. The program is the saved path, else the autodetected one, else
/// the user's Browse choice (saved). Read-only with respect to the run's files (Hard Rule 14). Never logs a path.
/// </summary>
public sealed class ExternalViewerOpener : IExternalViewerOpener
{
    private readonly IIgvBatchClient _igv;
    private readonly IViewerProcessLauncher _launcher;
    private readonly IViewerLocator _locator;
    private readonly ISettingsStore _settings;
    private readonly IFilePicker _picker;

    public ExternalViewerOpener(
        IIgvBatchClient igv,
        IViewerProcessLauncher launcher,
        IViewerLocator locator,
        ISettingsStore settings,
        IFilePicker picker)
    {
        _igv = igv;
        _launcher = launcher;
        _locator = locator;
        _settings = settings;
        _picker = picker;
    }

    public async Task<ExternalViewerOutcome> OpenInIgvAsync(IReadOnlyList<RunOutputFile> files, CancellationToken cancellationToken)
    {
        var igvFiles = ExternalViewerFiles.ForIgv(files);
        if (!igvFiles.HasTracks)
        {
            return ExternalViewerOutcome.NoFiles;
        }

        var batch = await _igv.SendAsync(ReadPort(), IgvBatchCommands.Build(igvFiles), cancellationToken);
        switch (batch)
        {
            case IgvBatchOutcome.Done:
                return ExternalViewerOutcome.Opened;
            case IgvBatchOutcome.Rejected:
                return ExternalViewerOutcome.IgvRejected;
            case IgvBatchOutcome.NoReply:
                return ExternalViewerOutcome.IgvNoReply;
            default:
                return await LaunchAsync(ExternalViewer.Igv, ViewerSettingKeys.IgvPath, IgvBatchCommands.LaunchArguments(igvFiles), cancellationToken);
        }
    }

    public async Task<ExternalViewerOutcome> OpenInGeneiousAsync(IReadOnlyList<RunOutputFile> files, CancellationToken cancellationToken)
    {
        var paths = ExternalViewerFiles.ForGeneious(files);
        return paths.Count == 0
            ? ExternalViewerOutcome.NoFiles
            : await LaunchAsync(ExternalViewer.Geneious, ViewerSettingKeys.GeneiousPath, paths, cancellationToken);
    }

    private async Task<ExternalViewerOutcome> LaunchAsync(ExternalViewer viewer, string key, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var program = await ResolveProgramAsync(viewer, key, cancellationToken);
        if (program is null)
        {
            return ExternalViewerOutcome.NotFound;
        }

        if (_launcher.Launch(program, arguments))
        {
            return ExternalViewerOutcome.Opened;
        }

        // A saved program that will not start is forgotten, so the next press looks again instead of failing the same way.
        if (!string.IsNullOrEmpty(Read(key)))
        {
            Write(key, string.Empty);
        }

        return ExternalViewerOutcome.LaunchFailed;
    }

    private async Task<string?> ResolveProgramAsync(ExternalViewer viewer, string key, CancellationToken cancellationToken)
    {
        var saved = Read(key);
        if (!string.IsNullOrWhiteSpace(saved) && File.Exists(saved))
        {
            return saved;
        }

        var found = _locator.Find(viewer);
        if (found is not null)
        {
            return found;
        }

        var picked = await _picker.PickProgramAsync(cancellationToken);
        if (string.IsNullOrEmpty(picked))
        {
            return null;
        }

        Write(key, picked);
        return picked;
    }

    private int ReadPort()
        => int.TryParse(Read(ViewerSettingKeys.IgvPort), NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535
            ? port
            : IgvBatchCommands.DefaultPort;

    // #558: a locked settings file must never stop a button; the viewer still opens, it is just not remembered.
    private string? Read(string key)
    {
        try
        {
            return _settings.GetString(key);
        }
        catch (SettingsUnavailableException ex)
        {
            System.Diagnostics.Trace.TraceWarning($"settings_unavailable while reading a viewer setting: {ex.GetType().Name}");
            return null;
        }
    }

    private void Write(string key, string value)
    {
        try
        {
            _settings.SetString(key, value);
        }
        catch (SettingsUnavailableException ex)
        {
            System.Diagnostics.Trace.TraceWarning($"settings_unavailable while saving a viewer setting: {ex.GetType().Name}");
        }
    }
}
