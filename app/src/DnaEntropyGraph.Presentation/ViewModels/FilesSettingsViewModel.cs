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

    public FilesSettingsViewModel(AppDataRoot root, IFolderLauncher launcher, IStringResourceProvider strings, IDispatcher dispatcher)
    {
        _root = root;
        _launcher = launcher;
        _strings = strings;
        Ready = MeasureAsync(dispatcher);
    }

    /// <summary>The summed size of every saved input copy; the page shows <see cref="InputsCacheSizeText"/>.</summary>
    public long InputsCacheBytes { get; private set; }

    /// <summary>Completes when the inputs cache size has been measured (off the UI thread: a large cache is many files).</summary>
    public Task Ready { get; }

    public string DataFolderPath => Path.GetFullPath(_root.Path);

    /// <summary>The folder that holds each run's saved input copy, under <c>runs\&lt;job&gt;\input</c>.</summary>
    public string InputsFolderPath => _root.RunsDirectory;

    public string LogsFolderPath => _root.LogsDirectory;

    /// <summary>Plain units: 0 KB, 1.5 KB, 5 MB, 3 GB.</summary>
    public static string FormatSize(long bytes)
    {
        const double Kb = 1024;
        const double Mb = Kb * 1024;
        const double Gb = Mb * 1024;
        if (bytes <= 0)
        {
            return "0 KB";
        }

        var (value, unit) = bytes >= Gb ? (bytes / Gb, "GB") : bytes >= Mb ? (bytes / Mb, "MB") : (Math.Max(bytes / Kb, 1), "KB");
        return value.ToString("0.#", CultureInfo.InvariantCulture) + " " + unit;
    }

    [RelayCommand]
    private void OpenDataFolder() => Open(DataFolderPath);

    [RelayCommand]
    private void OpenInputsFolder() => Open(InputsFolderPath);

    [RelayCommand]
    private void OpenLogsFolder() => Open(LogsFolderPath);

    private void Open(string folder)
        => FilesStatus = _launcher.OpenFolder(folder) ? string.Empty : _strings.GetString("FilesOpenFailed");

    private async Task MeasureAsync(IDispatcher dispatcher)
    {
        var bytes = await Task.Run(SumInputCopies).ConfigureAwait(false);
        dispatcher.Enqueue(() =>
        {
            InputsCacheBytes = bytes;
            OnPropertyChanged(nameof(InputsCacheBytes));
            InputsCacheSizeText = FormatSize(bytes);
        });
    }

    private long SumInputCopies()
    {
        long total = 0;
        try
        {
            if (!Directory.Exists(_root.RunsDirectory))
            {
                return 0;
            }

            foreach (var run in Directory.EnumerateDirectories(_root.RunsDirectory))
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

        return total;
    }
}
