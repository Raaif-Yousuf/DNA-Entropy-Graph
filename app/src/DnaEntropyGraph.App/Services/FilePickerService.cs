using DnaEntropyGraph.Core.Abstractions;

namespace DnaEntropyGraph.App.Services;

/// <summary>
/// Placeholder for the WinRT file/folder picker. The real implementation
/// needs an owner window handle via <c>IInitializeWithWindow</c>
/// (docs/ui_conventions.md); that wiring is a follow-up issue once
/// MainWindow exists as more than an empty shell.
/// </summary>
public sealed class FilePickerService : IFilePicker
{
    public Task<string?> PickInputFileAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);

    public Task<string?> PickOutputFolderAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);
}
