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

    private static readonly Dictionary<string, Comparison<GeneRecord>> Keys = new(StringComparer.Ordinal)
    {
        ["contig"] = (x, y) => NaturalStringComparer.Instance.Compare(x.Contig, y.Contig),
        ["gene"] = (x, y) => NaturalStringComparer.Instance.Compare(x.GeneId, y.GeneId),
        ["begin"] = (x, y) => x.Begin.CompareTo(y.Begin),
        ["end"] = (x, y) => x.End.CompareTo(y.End),
        ["strand"] = (x, y) => string.CompareOrdinal(x.Strand, y.Strand),
        ["length"] = (x, y) => x.LengthNt.CompareTo(y.LengthNt),
        ["mean"] = (x, y) => x.MeanEntropy.CompareTo(y.MeanEntropy),
        ["min"] = (x, y) => x.MinEntropy.CompareTo(y.MinEntropy),
        ["max"] = (x, y) => x.MaxEntropy.CompareTo(y.MaxEntropy),
        ["low"] = (x, y) => x.FractionLowEntropy.CompareTo(y.FractionLowEntropy),
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

    /// <summary>"500 of 1200 genes shown, sorted by Mean entropy" and where the rest is; empty when every gene is shown.</summary>
    public string TruncatedNotice => !IsTruncated
        ? string.Empty
        : string.Format(
            CultureInfo.CurrentCulture,
            _strings.GetString(ResultsCopy.GenesTruncated),
            MaxRowsShown,
            _records.Count,
            SortKey.Length == 0 ? _strings.GetString(ResultsCopy.GenesOrderFile) : string.Format(CultureInfo.CurrentCulture, _strings.GetString(ResultsCopy.GenesOrderSorted), _strings.GetString(ResultsCopy.GeneColumnKey(SortKey))));

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
        OnPropertyChanged(nameof(TruncatedNotice));

        // OrderBy is stable, so equal values keep the file's order in both directions.
        var comparer = Comparer<GeneRecord>.Create(select);
        Fill(SortDescending ? _records.OrderByDescending(g => g, comparer) : _records.OrderBy(g => g, comparer));
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

/// <summary>
/// Orders text the way a person reads gene names: digit runs by their number (g2 before g10), everything else by character code, so
/// the order is the same on every PC whatever its language setting. Equal numbers with different zero padding order by length.
/// </summary>
public sealed class NaturalStringComparer : IComparer<string>
{
    public static NaturalStringComparer Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        x ??= string.Empty;
        y ??= string.Empty;
        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
            {
                var si = i;
                var sj = j;
                while (i < x.Length && char.IsAsciiDigit(x[i]))
                {
                    i++;
                }

                while (j < y.Length && char.IsAsciiDigit(y[j]))
                {
                    j++;
                }

                var dx = x[si..i].TrimStart('0');
                var dy = y[sj..j].TrimStart('0');
                var byLength = dx.Length.CompareTo(dy.Length);
                if (byLength != 0)
                {
                    return byLength;
                }

                var byDigits = string.CompareOrdinal(dx, dy);
                if (byDigits != 0)
                {
                    return byDigits;
                }

                var byPadding = (i - si).CompareTo(j - sj);
                if (byPadding != 0)
                {
                    return byPadding;
                }
            }
            else
            {
                if (x[i] != y[j])
                {
                    return x[i].CompareTo(y[j]);
                }

                i++;
                j++;
            }
        }

        return (x.Length - i).CompareTo(y.Length - j);
    }
}
