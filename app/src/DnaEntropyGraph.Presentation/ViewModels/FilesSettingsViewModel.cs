using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Presentation.Services;

namespace DnaEntropyGraph.Presentation.ViewModels;

/// <summary>
/// The Settings "Files" group (issue #642): where the app keeps its own data. It shows the resolved app data folder (so a
/// <c>--profile</c> override shows its folder), the inputs cache (the saved copy of every input, Hard Rule 14) with its size,
/// and the logs folder, each with an Open folder button. It only reads sizes and opens folders: it deletes nothing, because
/// clearing the inputs cache would break Re-run and a run's outputs are never deleted without the user asking.
/// </summary>
public sealed partial class FilesSettingsViewModel : ObservableObject
{
    private readonly AppDataRoot _root;
    private readonly IFolderLauncher _launcher;
    private readonly IStringResourceProvider _strings;

    [ObservableProperty]
    private string _inputsCacheSizeText = string.Empty;

    [ObservableProperty]
    private string _filesStatus = string.Empty;

    /// <param name="measure">Tests only: replaces the folder scan, to prove an unexpected failure is handled.</param>
    public FilesSettingsViewModel(AppDataRoot root, IFolderLauncher launcher, IStringResourceProvider strings, IDispatcher dispatcher, Func<long>? measure = null)
    {
        _root = root;
        _launcher = launcher;
        _strings = strings;
        Ready = MeasureAsync(dispatcher, measure ?? SumInputCopies);
    }

    /// <summary>The summed size of every saved input copy; the page shows <see cref="InputsCacheSizeText"/>.</summary>
    public long InputsCacheBytes { get; private set; }

    /// <summary>Completes when the inputs cache size has been measured (off the UI thread: a large cache is many files).</summary>
    public Task Ready { get; }

    public string DataFolderPath => Path.GetFullPath(_root.Path);

    /// <summary>The folder that holds each run's saved input copy, under <c>runs\&lt;job&gt;\input</c>.</summary>
    public string InputsFolderPath => _root.RunsDirectory;

    public string LogsFolderPath => _root.LogsDirectory;

    /// <summary>Plain units: 0 KB, 1.5 KB, 5 MB, 3 GB. A value that rounds up to 1024 moves to the next unit ("1 MB", never "1024 KB").</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 KB";
        }

        string[] units = ["KB", "MB", "GB"];
        var value = Math.Max(bytes / 1024.0, 1);
        var unit = 0;
        while (unit < units.Length - 1 && Math.Round(value, 1, MidpointRounding.AwayFromZero) >= 1024)
        {
            value /= 1024;
            unit++;
        }

        return value.ToString("0.#", CultureInfo.InvariantCulture) + " " + units[unit];
    }

    [RelayCommand]
    private void OpenDataFolder() => Open(DataFolderPath);

    [RelayCommand]
    private void OpenInputsFolder() => Open(InputsFolderPath);

    [RelayCommand]
    private void OpenLogsFolder() => Open(LogsFolderPath);

    private void Open(string folder)
        => FilesStatus = _launcher.OpenFolder(folder) ? string.Empty : _strings.GetString("FilesOpenFailed");

    private async Task MeasureAsync(IDispatcher dispatcher, Func<long> measure)
    {
        try
        {
            var bytes = await Task.Run(measure).ConfigureAwait(false);
            dispatcher.Enqueue(() =>
            {
                InputsCacheBytes = bytes;
                OnPropertyChanged(nameof(InputsCacheBytes));
                InputsCacheSizeText = FormatSize(bytes);
            });
        }
        catch (Exception ex)
        {
            // Never the path or file names: the class only. Ready must complete, or the fault is unobserved and the size stays blank.
            System.Diagnostics.Trace.TraceWarning($"inputs cache size failed: {ex.GetType().Name}");
            dispatcher.Enqueue(() => InputsCacheSizeText = _strings.GetString("FilesSizeUnknown"));
        }
    }

    private long SumInputCopies()
    {
        long total = 0;
        try
        {
            foreach (var run in Directory.Exists(_root.RunsDirectory) ? Directory.EnumerateDirectories(_root.RunsDirectory) : [])
            {
                var input = Path.Combine(run, "input");
                if (!Directory.Exists(input))
                {
                    continue;
                }

                foreach (var file in new DirectoryInfo(input).EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    total += file.Length;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning($"inputs cache size could not be read: {ex.GetType().Name}");
        }

        return total + SumPastedCopies();
    }

    /// <summary>The pasted sequences are saved under <c>pasted\</c> before a run starts (LocalPastedInputStore), so they are input copies too.</summary>
    private long SumPastedCopies()
    {
        long total = 0;
        try
        {
            var pasted = Path.Combine(_root.Path, "pasted");
            if (Directory.Exists(pasted))
            {
                foreach (var file in new DirectoryInfo(pasted).EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    total += file.Length;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning($"pasted copies size could not be read: {ex.GetType().Name}");
        }

        return total;
    }
}
