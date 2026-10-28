using DnaEntropyGraph.Core.Tracks;
using DnaEntropyGraph.Presentation.Services;
using DnaEntropyGraph.Presentation.ViewModels;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

/// <summary>Issue #552: the sortable gene table on the Results page.</summary>
public sealed class ResultsGeneTableTests
{
    private static GeneRecord Gene(string id, double mean, long begin = 1, long length = 30, string contig = "c1")
        => new(contig, id, begin, begin + length - 1, "+", length, false, false, mean, mean - 0.1, mean + 0.1, null, 0.25);

    private static IStringResourceProvider Strings()
    {
        var strings = Substitute.For<IStringResourceProvider>();
        strings.GetString(Arg.Any<string>()).Returns(ci => ci.Arg<string>());
        return strings;
    }

    private static ResultsGeneTable Table(params GeneRecord[] rows) => new("t", rows, Strings());

    private static string[] Ids(ResultsGeneTable t) => [.. t.Rows.Select(r => r.GeneId)];

    [Fact]
    public void Rows_start_in_the_files_own_order()
        => Ids(Table(Gene("b", 1.5), Gene("a", 0.5), Gene("c", 1.0))).ShouldBe(["b", "a", "c"]);

    [Fact]
    public void Sorting_by_mean_entropy_is_numeric_ascending_first_then_descending()
    {
        // 10.0 would sort before 9.0 if the column were sorted as text.
        var t = Table(Gene("big", 1.9), Gene("low", 0.25), Gene("mid", 1.25));

        t.SortByCommand.Execute("mean");
        Ids(t).ShouldBe(["low", "mid", "big"]);

        t.SortByCommand.Execute("mean");
        Ids(t).ShouldBe(["big", "mid", "low"]);
    }

    [Fact]
    public void Sorting_by_length_compares_numbers_not_text()
    {
        var t = Table(Gene("nine", 1, length: 9), Gene("ten", 1, length: 10), Gene("hundred", 1, length: 100));

        t.SortByCommand.Execute("length");

        Ids(t).ShouldBe(["nine", "ten", "hundred"]);
    }

    [Fact]
    public void Ties_keep_the_files_order_in_both_directions()
    {
        var t = Table(Gene("a", 1), Gene("b", 1), Gene("c", 0.5));

        t.SortByCommand.Execute("mean");
        Ids(t).ShouldBe(["c", "a", "b"]);
        t.SortByCommand.Execute("mean");
        Ids(t).ShouldBe(["a", "b", "c"]);
    }

    [Fact]
    public void Choosing_another_column_starts_ascending_again()
    {
        var t = Table(Gene("b", 1, begin: 50), Gene("a", 2, begin: 10));
        t.SortByCommand.Execute("mean");
        t.SortByCommand.Execute("mean");

        t.SortByCommand.Execute("begin");

        Ids(t).ShouldBe(["a", "b"]);
        t.SortDescending.ShouldBeFalse();
    }

    [Fact]
    public void An_unknown_column_changes_nothing()
    {
        var t = Table(Gene("b", 2), Gene("a", 1));

        t.SortByCommand.Execute("nonsense");

        Ids(t).ShouldBe(["b", "a"]);
    }

    [Fact]
    public void A_gene_row_shows_its_numbers_in_bits_to_two_decimals()
    {
        var row = Table(Gene("g", 1.256, begin: 10, length: 90)).Rows.Single();

        row.MeanText.ShouldBe("1.26");
        row.BeginText.ShouldBe("10");
        row.EndText.ShouldBe("99");
        row.LengthText.ShouldBe("90");
    }

    [Fact]
    public void A_very_long_table_shows_the_first_rows_and_says_where_the_rest_is()
    {
        var t = Table([.. Enumerable.Range(0, ResultsGeneTable.MaxRowsShown + 5).Select(i => Gene("g" + i, i))]);

        t.Rows.Count.ShouldBe(ResultsGeneTable.MaxRowsShown);
        t.IsTruncated.ShouldBeTrue();
        t.TruncatedNotice.ShouldBe(ResultsCopy.GenesTruncated);

        t.SortByCommand.Execute("mean");
        t.SortByCommand.Execute("mean");
        t.Rows[0].GeneId.ShouldBe("g" + (ResultsGeneTable.MaxRowsShown + 4), "sorting applies to every gene, not only the ones shown");
    }
}
