using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DnaEntropyGraph.Core.Tracks;
using DnaEntropyGraph.Presentation.Services;

namespace DnaEntropyGraph.Presentation.ViewModels;

/// <summary>One gene row of the Results gene table, formatted for display (bits to two decimals).</summary>
public sealed record GeneRowItem(
    string Contig,
    string GeneId,
    string BeginText,
    string EndText,
    string Strand,
    string LengthText,
    string MeanText,
    string MinText,
    string MaxText,
    string LowText);

/// <summary>
/// One <c>*.genes.tsv</c> as a sortable table (issue #552). Sorting is on the numbers the worker wrote, never on the shown text, so
/// "mean entropy" orders the way a spreadsheet orders the file's own column. The first click on a column sorts ascending, a second
/// click on the same column reverses it; ties keep the file's own order. At most <see cref="MaxRowsShown"/> rows are drawn (the sort
/// still covers every gene); the file in the list below holds them all. No WinUI types: tested without a UI thread.
/// </summary>
public sealed partial class ResultsGeneTable : ObservableObject
{
    /// <summary>Drawing thousands of rows in a non-virtualised list would stall the page; the full table is in the file.</summary>
    public const int MaxRowsShown = 500;

    private static readonly Dictionary<string, Func<GeneRecord, IComparable>> Keys = new(StringComparer.Ordinal)
    {
        ["contig"] = g => g.Contig,
        ["gene"] = g => g.GeneId,
        ["begin"] = g => g.Begin,
        ["end"] = g => g.End,
        ["strand"] = g => g.Strand,
        ["length"] = g => g.LengthNt,
        ["mean"] = g => g.MeanEntropy,
        ["min"] = g => g.MinEntropy,
        ["max"] = g => g.MaxEntropy,
        ["low"] = g => g.FractionLowEntropy,
    };

    private readonly IReadOnlyList<GeneRecord> _records;
    private readonly IStringResourceProvider _strings;

    [ObservableProperty]
    private string _sortKey = string.Empty;

    [ObservableProperty]
    private bool _sortDescending;

    public ResultsGeneTable(string title, IReadOnlyList<GeneRecord> records, IStringResourceProvider strings)
    {
        Title = title;
        _records = records;
        _strings = strings;
        Fill(records);
    }

    /// <summary>The file this table came from (shown only when the run has several).</summary>
    public string Title { get; }

    public bool HasTitle => Title.Length > 0;

    public ObservableCollection<GeneRowItem> Rows { get; } = [];

    public bool IsTruncated => _records.Count > MaxRowsShown;

    public string TruncatedNotice => IsTruncated ? _strings.GetString(ResultsCopy.GenesTruncated) : string.Empty;

    /// <summary>Header text of the column the table is sorted by, with its direction, for the line above the table.</summary>
    public string SortSummary => SortKey.Length == 0
        ? string.Empty
        : string.Format(CultureInfo.CurrentCulture, _strings.GetString(SortDescending ? ResultsCopy.GenesSortedDescending : ResultsCopy.GenesSortedAscending), _strings.GetString(ResultsCopy.GeneColumnKey(SortKey)));

    [RelayCommand]
    private void SortBy(string? key)
    {
        if (key is null || !Keys.TryGetValue(key, out var select))
        {
            return;
        }

        SortDescending = SortKey == key && !SortDescending;
        SortKey = key;
        OnPropertyChanged(nameof(SortSummary));

        // OrderBy is stable, so equal values keep the file's order in both directions.
        Fill(SortDescending ? _records.OrderByDescending(select) : _records.OrderBy(select));
    }

    private void Fill(IEnumerable<GeneRecord> ordered)
    {
        Rows.Clear();
        foreach (var g in ordered.Take(MaxRowsShown))
        {
            Rows.Add(new GeneRowItem(
                g.Contig,
                g.GeneId,
                g.Begin.ToString(CultureInfo.CurrentCulture),
                g.End.ToString(CultureInfo.CurrentCulture),
                g.Strand,
                g.LengthNt.ToString(CultureInfo.CurrentCulture),
                Bits(g.MeanEntropy),
                Bits(g.MinEntropy),
                Bits(g.MaxEntropy),
                g.FractionLowEntropy.ToString("P0", CultureInfo.CurrentCulture)));
        }
    }

    private static string Bits(double value) => value.ToString("F2", CultureInfo.CurrentCulture);
}
