using DnaEntropyGraph.Core.Inputs;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests.Inputs;

/// <summary>Issue #684: an NCBI accession is checked here, with the worker's own pattern, before any cloud resource exists (Hard Rule 2).</summary>
public sealed class AccessionInputTests
{
    [Theory]
    [InlineData("NC_045512.2", "NC_045512.2")]
    [InlineData("MN908947.3", "MN908947.3")]
    [InlineData("  mn908947  ", "MN908947")]
    [InlineData("NZ_CP012345", "NZ_CP012345")]
    [InlineData("U00096.3", "U00096.3")]
    public void A_real_accession_is_accepted_trimmed_and_upper_cased(string typed, string expected)
    {
        var check = AccessionInput.Check(typed, null, null);

        check.IsValid.ShouldBeTrue();
        check.Accession.ShouldBe(expected);
        check.Range.ShouldBeNull();
        check.Problem.ShouldBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("hello world")]
    [InlineData("NC_045512.2&db=nuccore")]
    [InlineData("NC_045512.2\n")]
    [InlineData("../NC_045512")]
    [InlineData("NC_045512.1234")]
    [InlineData("12345")]
    [InlineData("NC_04")]
    public void Anything_that_is_not_an_accession_is_refused_with_the_not_an_accession_problem(string typed)
    {
        var check = AccessionInput.Check(typed, null, null);

        check.IsValid.ShouldBe(typed.Trim() == "NC_045512.2\n".Trim(), "only the trailing-newline form is a real accession once trimmed");
        if (!check.IsValid)
        {
            check.Problem.ShouldBe(AccessionProblem.NotAnAccession);
        }
    }

    [Fact]
    public void Non_ascii_look_alikes_are_refused()
    {
        // Arabic-Indic digits match \d unless the pattern is ASCII only; a dotless i upper-cases to an ASCII I.
        AccessionInput.Check("NC_٤٥٦٧٨٩.2", null, null).Problem.ShouldBe(AccessionProblem.NotAnAccession);
        AccessionInput.Check("ıN_045512", null, null).Problem.ShouldBe(AccessionProblem.NotAnAccession);
    }

    [Fact]
    public void A_range_is_sent_as_start_dash_end()
    {
        var check = AccessionInput.Check("NC_045512.2", " 100 ", "500");

        check.IsValid.ShouldBeTrue();
        check.Range.ShouldBe("100-500");
    }

    [Theory]
    [InlineData("100", "")]
    [InlineData("", "500")]
    [InlineData("100", null)]
    public void Half_a_range_is_refused(string? start, string? end)
        => AccessionInput.Check("NC_045512.2", start, end).Problem.ShouldBe(AccessionProblem.RangeIncomplete);

    [Theory]
    [InlineData("0", "10")]
    [InlineData("500", "100")]
    [InlineData("a", "10")]
    [InlineData("1", "-5")]
    [InlineData("1.5", "10")]
    [InlineData("١٠", "500")]
    [InlineData("99999999999999999999", "100000000000000000000")]
    public void A_range_that_is_not_one_to_start_to_end_is_refused(string start, string end)
        => AccessionInput.Check("NC_045512.2", start, end).Problem.ShouldBe(AccessionProblem.RangeInvalid);

    [Fact]
    public void Blank_range_boxes_mean_the_whole_record()
    {
        var check = AccessionInput.Check("NC_045512.2", "", "  ");

        check.IsValid.ShouldBeTrue();
        check.Range.ShouldBeNull();
    }

    [Fact]
    public void A_bad_accession_is_reported_before_a_bad_range()
        => AccessionInput.Check("nope", "5", "1").Problem.ShouldBe(AccessionProblem.NotAnAccession);
}
