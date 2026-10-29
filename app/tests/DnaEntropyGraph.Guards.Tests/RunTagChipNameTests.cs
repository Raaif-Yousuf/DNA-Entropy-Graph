using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>Issue #141: every tag chip on the Runs page must be announced by its own text, not by one shared word.</summary>
public sealed class RunTagChipNameTests
{
    private static string Page() => File.ReadAllText(Path.Combine(RepoPaths.AppRoot, "src", "DnaEntropyGraph.App", "Views", "RunsPage.xaml"));

    [Fact]
    public void The_tag_chip_is_named_by_the_tag_text_and_not_by_a_shared_resource()
    {
        var page = Page();

        page.ShouldContain("AutomationProperties.Name=\"{x:Bind}\"");
        page.ShouldNotContain("x:Uid=\"RunsTagChip\"");
    }
}
