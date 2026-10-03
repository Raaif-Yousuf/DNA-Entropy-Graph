using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using Windows.Storage.Pickers;

namespace DnaEntropyGraph.App.Services;

/// <summary>
/// The WinRT file picker, owned by the main window through <see cref="WindowHandleProvider"/>
/// (a desktop app's picker throws without an owner window handle). The output-folder picker
/// is still a placeholder (a follow-up issue).
/// </summary>
public sealed class FilePickerService(WindowHandleProvider window) : IFilePicker
{
    private static readonly string[] SequenceExtensions =
        [".fa", ".fasta", ".fna", ".ffn", ".gb", ".gbk", ".genbank", ".gbff", ".txt"];

    public async Task<IReadOnlyList<string>> PickInputFilesAsync(CancellationToken cancellationToken)
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, window.Hwnd);
        foreach (var extension in SequenceExtensions)
        {
            picker.FileTypeFilter.Add(extension);
        }

        var files = await picker.PickMultipleFilesAsync();
        return files.Select(file => file.Path).ToList();
    }

    public Task<string?> PickOutputFolderAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);
}
