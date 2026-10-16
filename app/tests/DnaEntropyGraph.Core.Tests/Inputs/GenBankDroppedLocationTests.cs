using DnaEntropyGraph.Core.Inputs;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests.Inputs;

/// <summary>
/// Issues #543 and #788 (Hard Rule 2). Biopython sets the location of a feature it cannot parse to None and only
/// warns, and the worker drops that feature with ONE counted notice per record; for the few shapes where an
/// exception escapes the parser, the worker refuses the file. The local validator must reach the same verdict and
/// show the same notice, or it would refuse a file the worker can run (the bug) or hide a lost gene.
/// <see cref="GenBankLocationMeasured" /> is the ground truth: Biopython's own verdict on ~110 location strings.
/// </summary>
public sealed class GenBankDroppedLocationTests
{
    private const string Sample = """
        LOCUS       toy_locus                126 bp    DNA              UNK 01-JAN-1980
        DEFINITION  Toy locus.
        ACCESSION   toy_locus
        VERSION     toy_locus
        KEYWORDS    .
        SOURCE      .
          ORGANISM  .
                    .
        FEATURES             Location/Qualifiers
             gene            1..42
                             /gene="geneA"
                             /locus_tag="TOY_0001"
             gene            complement(85..126)
                             /gene="geneB"
                             /locus_tag="TOY_0002"
             CDS             1..42
                             /gene="geneA"
                             /product="hypothetical protein"
        ORIGIN
                1 atgcgtacgt tagcactgac cggtattccg aaactggcgt aaccggttaa ccggttaacc
               61 ggttaaccgg ttaaccggtt aaccatgaaa cgttttggca cctaaggcct taaggcctta
              121 aggcct
        //

        """;

    private const string GeneLine = "     gene            1..42\n";

    private static string Normalize(string s) => s.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string WithFirstGeneAt(string location, bool circular = false)
    {
        var text = Normalize(Sample).Replace(GeneLine, $"     gene            {location}\n", StringComparison.Ordinal);
        return circular ? text.Replace("DNA              UNK", "DNA     circular UNK", StringComparison.Ordinal) : text;
    }

    public static IEnumerable<object[]> MeasuredRows() => GenBankLocationMeasured.Rows
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(line => line.Split('|'))
        .Select(p => new object[] { p[0], p[1] == "circ", p[2], p[3] });

    [Theory]
    [MemberData(nameof(MeasuredRows))]
    public void Every_location_gets_the_verdict_Biopython_gives_it(string location, bool circular, string verdict, string detail)
    {
        var text = WithFirstGeneAt(location, circular);

        if (verdict == "REFUSE")
        {
            Should.Throw<GenBankReadException>(() => GenBankLite.Read(text), $"{location} ({(circular ? "circular" : "linear")}) is refused by the worker ({detail})");
            return;
        }

        var result = GenBankLite.Read(text);
        var dropped = result.NoticeCodes.Where(n => n.Code == InputNoticeCode.GenBankFeaturesDropped).ToList();
        if (verdict == "OK")
        {
            dropped.ShouldBeEmpty($"{location} ({(circular ? "circular" : "linear")}) is readable");
            return;
        }

        var notice = dropped.ShouldHaveSingleItem($"{location} is dropped by the worker");
        notice.Count.ShouldBe(1);
        notice.Other.ShouldBe(1); // the record number
        result.Notices.ShouldContain(n => n.Contains($"(gene '{detail}')", StringComparison.Ordinal), $"the worker names the quoted text '{detail}'");
    }

    [Fact]
    public void The_parity_fixture_is_accepted_and_carries_the_one_notice()
    {
        var path = Path.Combine(ContractFixtures.RepoRoot, "worker", "tests", "data", "malformed", "genbank_unparseable_location.gb");

        var result = InputFileValidator.Validate(path, InputFormat.Auto, AmbiguityPolicy.Keep, treatAsRna: false);

        result.IsValid.ShouldBeTrue(result.Problem?.Detail);
        var notice = result.NoticeCodes.Where(n => n.Code == InputNoticeCode.GenBankFeaturesDropped).ShouldHaveSingleItem();
        notice.Count.ShouldBe(1); // gene 'abc' only: the CDS '0..42' was never going to be a gene, the record has genes
        notice.Other.ShouldBe(1);
        result.GeneCount.ShouldBe(1); // the pill counts the readable gene, not the one that is left out
        result.Notices.ShouldContain(
            "GenBank record 1: 1 gene feature(s) were dropped because their location text could not be read (gene 'abc'). "
            + "A gene with an unreadable location is missing from the exported tracks. Correct the location in the source file "
            + "(for example 1..42 or complement(85..126)) and run again.");
        result.NoticeCodes.Count.ShouldBe(result.Notices.Count);
    }

    [Fact]
    public void CDS_features_are_counted_when_the_record_has_no_gene_features()
    {
        var text = Normalize(Sample)
            .Replace(GeneLine, "     misc_feature    1..42\n", StringComparison.Ordinal)
            .Replace("     gene            complement(85..126)\n", "     misc_feature    85..126\n", StringComparison.Ordinal)
            .Replace("     CDS             1..42\n", "     CDS             abc\n", StringComparison.Ordinal);

        var result = GenBankLite.Read(text);

        result.NoticeCodes.Where(n => n.Code == InputNoticeCode.GenBankFeaturesDropped).ShouldHaveSingleItem().Count.ShouldBe(1);
        result.Notices.ShouldContain(n => n.Contains("1 CDS feature(s) were dropped", StringComparison.Ordinal) && n.Contains("(CDS 'abc')", StringComparison.Ordinal));
    }

    [Fact]
    public void A_dropped_CDS_is_not_counted_when_a_readable_gene_exists()
    {
        var text = Normalize(Sample).Replace("     CDS             1..42\n", "     CDS             abc\n", StringComparison.Ordinal);

        GenBankLite.Read(text).NoticeCodes.ShouldNotContain(n => n.Code == InputNoticeCode.GenBankFeaturesDropped);
    }

    [Fact]
    public void A_well_formed_file_has_no_such_notice()
        => GenBankLite.Read(Normalize(Sample)).NoticeCodes.ShouldNotContain(n => n.Code == InputNoticeCode.GenBankFeaturesDropped);

    [Fact]
    public void One_notice_per_record_each_with_its_own_record_number()
    {
        var one = Normalize(Sample).Replace(GeneLine, "     gene            abc\n", StringComparison.Ordinal);

        var result = GenBankLite.Read(one + one);

        var found = result.NoticeCodes.Where(n => n.Code == InputNoticeCode.GenBankFeaturesDropped).ToList();
        found.Select(n => n.Other).ShouldBe([1L, 2L]);
        result.Notices.ShouldContain(n => n.StartsWith("GenBank record 1: ", StringComparison.Ordinal));
        result.Notices.ShouldContain(n => n.StartsWith("GenBank record 2: ", StringComparison.Ordinal));
    }

    [Fact]
    public void A_record_that_is_skipped_for_having_no_sequence_makes_no_dropped_notice()
    {
        var empty = Normalize(Sample).Replace(GeneLine, "     gene            abc\n", StringComparison.Ordinal);
        empty = empty[..empty.IndexOf("ORIGIN", StringComparison.Ordinal)] + "ORIGIN\n//\n";

        var result = GenBankLite.Read(empty + Normalize(Sample));

        result.NoticeCodes.ShouldNotContain(n => n.Code == InputNoticeCode.GenBankFeaturesDropped);
        result.NoticeCodes.ShouldContain(n => n.Code == InputNoticeCode.GenBankRecordSkippedNoSequence);
    }

    [Fact]
    public void Many_dropped_features_are_counted_and_the_text_lists_three()
    {
        var extra = string.Concat(Enumerable.Range(0, 8).Select(i => $"     gene            bad{i}\n                     /gene=\"g{i}\"\n"));
        var text = Normalize(Sample).Replace("FEATURES             Location/Qualifiers\n", "FEATURES             Location/Qualifiers\n" + extra, StringComparison.Ordinal);

        var result = GenBankLite.Read(text);

        result.NoticeCodes.Where(n => n.Code == InputNoticeCode.GenBankFeaturesDropped).ShouldHaveSingleItem().Count.ShouldBe(8);
        var english = result.Notices.Single(n => n.Contains("could not be read", StringComparison.Ordinal));
        english.ShouldContain("8 gene feature(s)");
        english.ShouldContain("(gene 'bad0', gene 'bad1', gene 'bad2', and 5 more)");
        english.ShouldNotContain("bad7");
    }

    [Fact]
    public void The_location_text_is_cut_at_forty_characters_and_escaped_to_ASCII_like_the_worker()
    {
        var longText = new string('x', 200);

        GenBankLite.Read(WithFirstGeneAt("éα..5")).Notices
            .ShouldContain(n => n.Contains("(gene '\\xe9\\u03b1..5')", StringComparison.Ordinal));
        var english = GenBankLite.Read(WithFirstGeneAt(longText)).Notices.Single(n => n.Contains("could not be read", StringComparison.Ordinal));
        english.ShouldContain($"(gene '{new string('x', 37)}...')");
        english.ShouldNotContain(longText);
        english.All(c => c < 128).ShouldBeTrue();
    }

    [Fact]
    public void A_refused_location_names_the_record_and_one_action_and_never_echoes_the_text()
    {
        var ex = Should.Throw<GenBankReadException>(() => GenBankLite.Read(WithFirstGeneAt("join()")));

        ex.Message.ShouldStartWith("Could not parse the GenBank file (record 1): ");
        ex.Message.ShouldContain("re-export it from the tool that made it");
        ex.Message.ShouldNotContain("join()");
    }

    [Fact]
    public void Features_of_any_key_are_parsed_so_a_bad_misc_feature_refuses_the_file_as_the_worker_does()
    {
        var text = Normalize(Sample).Replace(GeneLine, "     misc_feature    1..5,9..12\n", StringComparison.Ordinal);

        Should.Throw<GenBankReadException>(() => GenBankLite.Read(text));
    }

    [Fact]
    public void The_topology_comes_from_the_LOCUS_line_in_the_space_separated_style_too()
    {
        // Ensembl-style LOCUS lines are space separated; Biopython still reads the length and the topology.
        var text = Normalize(Sample).Replace(
            "LOCUS       toy_locus                126 bp    DNA              UNK 01-JAN-1980",
            "LOCUS       toy_locus 126 bp DNA circular UNK 01-JAN-1980",
            StringComparison.Ordinal).Replace(GeneLine, "     gene            42..1\n", StringComparison.Ordinal);

        GenBankLite.Read(text).NoticeCodes.ShouldNotContain(n => n.Code == InputNoticeCode.GenBankFeaturesDropped);
    }
}
