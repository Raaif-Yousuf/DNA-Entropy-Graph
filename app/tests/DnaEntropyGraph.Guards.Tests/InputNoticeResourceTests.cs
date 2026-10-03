using System.Xml.Linq;
using DnaEntropyGraph.Core.Inputs;
using DnaEntropyGraph.Presentation.ViewModels;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>Hard Rule 13 for validation notices (review of #63): each notice code has plain-words copy in Resources.resw.</summary>
public class InputNoticeResourceTests
{
    [Fact]
    public void Every_notice_code_the_pill_shows_has_resw_copy_without_an_em_dash()
    {
        var resw = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in RepoPaths.AllReswFiles)
        {
            foreach (var data in XDocument.Load(file).Descendants("data"))
            {
                resw[data.Attribute("name")!.Value] = data.Element("value")?.Value ?? string.Empty;
            }
        }

        var codes = Enum.GetValues<InputNoticeCode>();
        codes.Length.ShouldBeGreaterThan(8);
        var shown = 0;
        foreach (var code in codes)
        {
            var key = InputNoticeCopy.KeyFor(code);
            if (key is null)
            {
                continue;
            }

            shown++;
            resw.ShouldContainKey(key, $"notice code {code} maps to {key}, which Resources.resw does not define");
            resw[key].Trim().ShouldNotBeEmpty();
            resw[key].ShouldNotContain("\u2014");
        }

        shown.ShouldBeGreaterThan(6);
    }

    [Fact]
    public void Every_notice_that_counts_something_has_singular_copy_for_a_count_of_one()
    {
        var resw = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in RepoPaths.AllReswFiles)
        {
            foreach (var data in XDocument.Load(file).Descendants("data"))
            {
                resw[data.Attribute("name")!.Value] = data.Element("value")?.Value ?? string.Empty;
            }
        }

        var counted = Enum.GetValues<InputNoticeCode>().Where(code => InputNoticeCopy.KeyFor(code, 1) != InputNoticeCopy.KeyFor(code)).ToList();
        counted.ShouldBe(
            [
                InputNoticeCode.EmptyHeaders,
                InputNoticeCode.RepeatedIds,
                InputNoticeCode.RnaConverted,
                InputNoticeCode.AmbiguityMasked,
                InputNoticeCode.AmbiguityKept,
                InputNoticeCode.ShortSequence,
                InputNoticeCode.DigitsRemoved,
            ],
            ignoreOrder: true);
        foreach (var code in counted)
        {
            var key = InputNoticeCopy.KeyFor(code, 1)!;
            resw.ShouldContainKey(key, $"notice code {code} has no singular copy {key}");
            resw[key].Trim().ShouldNotBeEmpty();
            resw[key].ShouldNotContain("—");
            InputNoticeCopy.KeyFor(code, 2).ShouldBe(InputNoticeCopy.KeyFor(code));
        }
    }

    [Fact]
    public void Only_the_two_summary_notices_are_left_out_of_the_pill()
        => Enum.GetValues<InputNoticeCode>().Where(code => InputNoticeCopy.KeyFor(code) is null)
            .ShouldBe([InputNoticeCode.RecordsRead, InputNoticeCode.GenBankRead], ignoreOrder: true);
}
