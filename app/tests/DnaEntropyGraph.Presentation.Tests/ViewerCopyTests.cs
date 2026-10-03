using System.Text.RegularExpressions;
using DnaEntropyGraph.Presentation.Viewer;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

public sealed class ViewerCopyTests
{
    public static TheoryData<ViewerFailure> AllFailures()
    {
        var data = new TheoryData<ViewerFailure>();
        foreach (var f in Enum.GetValues<ViewerFailure>())
        {
            data.Add(f);
        }

        return data;
    }

    [Fact]
    public void Every_failure_has_a_mapping()
    {
        // A new ViewerFailure value with no row in the switch would otherwise show a blank error.
        foreach (var f in Enum.GetValues<ViewerFailure>())
        {
            Should.NotThrow(() => ViewerCopy.For(f));
        }
    }

    [Theory]
    [MemberData(nameof(AllFailures))]
    public void Each_failure_names_a_title_and_a_body_in_resw(ViewerFailure failure)
    {
        var copy = ViewerCopy.For(failure);
        var keys = ReswKeys();
        keys.ShouldContain(copy.TitleKey);
        keys.ShouldContain(copy.BodyKey);
        if (copy.ActionKey is not null)
        {
            keys.ShouldContain(copy.ActionKey);
        }
    }

    [Fact]
    public void Failure_keys_are_distinct()
    {
        var all = Enum.GetValues<ViewerFailure>().Select(ViewerCopy.For).ToList();
        all.Select(c => c.TitleKey).Distinct().Count().ShouldBe(all.Count);
        all.Select(c => c.BodyKey).Distinct().Count().ShouldBe(all.Count);
    }

    [Theory]
    [InlineData(ViewerFailure.RuntimeMissing, "ViewerRuntimeMissing_Title", "ViewerRuntimeMissing_Body", "ViewerRuntimeMissing_Action")]
    [InlineData(ViewerFailure.ViewerError, "ViewerError_Title", "ViewerError_Body", "ViewerError_Action")]
    [InlineData(ViewerFailure.ProcessFailed, "ViewerProcessFailed_Title", "ViewerProcessFailed_Body", null)]
    [InlineData(ViewerFailure.InitFailed, "ViewerInitFailed_Title", "ViewerInitFailed_Body", null)]
    [InlineData(ViewerFailure.AmbiguousSequence, "ViewerAmbiguousSequence_Title", "ViewerAmbiguousSequence_Body", null)]
    public void Keys_match_the_reviewed_copy(ViewerFailure failure, string title, string body, string? action)
    {
        var copy = ViewerCopy.For(failure);
        copy.TitleKey.ShouldBe(title);
        copy.BodyKey.ShouldBe(body);
        copy.ActionKey.ShouldBe(action);
    }

    private static HashSet<string> ReswKeys()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var path = Path.Combine(dir.FullName, "app", "src", "DnaEntropyGraph.App", "Strings", "en-US", "Resources.resw");
            if (File.Exists(path))
            {
                return Regex.Matches(File.ReadAllText(path), "<data name=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToHashSet();
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException("Resources.resw not found above " + AppContext.BaseDirectory);
    }
}
