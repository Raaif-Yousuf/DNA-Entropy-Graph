using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Inputs;
using DnaEntropyGraph.Presentation.Services;
using Windows.Storage.Pickers;

namespace DnaEntropyGraph.App.Services;

/// <summary>
/// The WinRT file picker, owned by the main window through <see cref="WindowHandleProvider"/>
/// (a desktop app's picker throws without an owner window handle). The output-folder picker
/// is still a placeholder (a follow-up issue).
/// </summary>
public sealed class FilePickerService(WindowHandleProvider window, IStringResourceProvider strings) : IFilePicker
{
    public async Task<IReadOnlyList<string>> PickInputFilesAsync(CancellationToken cancellationToken)
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, window.Hwnd);
        foreach (var extension in SequenceFileTypes.Extensions)
        {
            picker.FileTypeFilter.Add(extension);
        }

        var files = await picker.PickMultipleFilesAsync();
        // A file with no local path (a cloud-only placeholder) cannot be read, so it is not offered on.
        return files.Select(file => file.Path).Where(path => !string.IsNullOrEmpty(path)).ToList();
    }

    public async Task<string?> PickSaveZipAsync(string suggestedFileName, CancellationToken cancellationToken)
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.Downloads,
            SuggestedFileName = Path.GetFileNameWithoutExtension(suggestedFileName),
        };
        WinRT.Interop.InitializeWithWindow.Initialize(picker, window.Hwnd);
        picker.FileTypeChoices.Add(strings.GetString("DiagnosticsFileTypeLabel"), [".zip"]);

        var file = await picker.PickSaveFileAsync();
        return string.IsNullOrEmpty(file?.Path) ? null : file.Path;
    }

    public Task<string?> PickOutputFolderAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);
}
