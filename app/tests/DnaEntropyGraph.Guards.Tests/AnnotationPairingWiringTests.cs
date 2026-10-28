using System.Xml.Linq;
using DnaEntropyGraph.Core.Inputs;
using DnaEntropyGraph.Presentation.ViewModels;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// Issue #686: the GFF3 pairing rests on XAML lines (the buttons and the pill's lines) and on copy for every reason a pair is
/// refused. A button nobody binds, or a problem code with no words, shows nothing and fails nothing.
/// </summary>
public sealed class AnnotationPairingWiringTests
{
    private static string Page() => File.ReadAllText(Path.Combine(RepoPaths.AppRoot, "src", "DnaEntropyGraph.App", "Views", "NewRunPage.xaml"));

    [Theory]
    [InlineData("Command=\"{x:Bind ViewModel.AddAnnotationCommand}\"")]
    [InlineData("Command=\"{x:Bind ViewModel.RemoveAnnotationCommand}\"")]
    [InlineData("Text=\"{x:Bind AnnotationText, Mode=OneWay}\"")]
    [InlineData("Text=\"{x:Bind AnnotationNoticesText, Mode=OneWay}\"")]
    [InlineData("Visibility=\"{x:Bind HasAnnotation, Mode=OneWay}\"")]
    public void The_New_run_page_binds_the_pairing_pieces(string fragment)
        => Page().ShouldContain(fragment);

    [Fact]
    public void Every_reason_a_GFF3_is_refused_has_copy_that_names_an_action_without_an_em_dash()
    {
        var resw = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in RepoPaths.AllReswFiles)
        {
            foreach (var data in XDocument.Load(file).Descendants("data"))
            {
                resw[data.Attribute("name")!.Value] = data.Element("value")?.Value ?? string.Empty;
            }
        }

        foreach (var code in Enum.GetValues<AnnotationProblemCode>())
        {
            var key = AnnotationProblemCopy.KeyFor(code);
            resw.ShouldContainKey(key, $"{code} maps to {key}, which Resources.resw does not define");
            resw[key].Trim().ShouldNotBeEmpty();
            resw[key].ShouldNotContain("—");
        }

        foreach (var key in new[] { "NewRunStatusAnnotationUnavailable", "NewRunStatusAnnotationNoFasta", "NewRunStatusAnnotationAmbiguous", "NewRunPillAnnotationLine", "NewRunStatusAnnotationAmbiguousName", "NewRunStatusAnnotationTwoForOne", "NewRunStatusAnnotationAlreadyPaired", "NewRunStatusAnnotationPairedOnly", "NewRunStatusAnnotationNamedInvalid", "NewRunStatusAnnotationFallbackRefused" })
        {
            resw.ShouldContainKey(key);
            resw[key].ShouldNotContain("—");
        }
    }
}
