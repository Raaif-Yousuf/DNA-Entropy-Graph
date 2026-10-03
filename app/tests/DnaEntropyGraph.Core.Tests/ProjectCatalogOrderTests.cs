using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests;

/// <summary>Issue #50: "prefer projects labelled app=dna-entropy-graph" is one ordering rule shared by the real gateway and the fake.</summary>
public class ProjectCatalogOrderTests
{
    private static ProjectSummary P(string id, string name, bool app, ProjectLifecycleState state = ProjectLifecycleState.Active)
        => new(id, name, state, app);

    [Fact]
    public void App_projects_come_first_then_by_name_then_id()
    {
        var ordered = ProjectCatalogOrder.Prefer(
        [
            P("zeta-1", "Zeta", false),
            P("alpha-1", "Alpha", false),
            P("dna-entropy-bbbbbbbb", "DNA Entropy Graph", true),
            P("dna-entropy-aaaaaaaa", "DNA Entropy Graph", true),
        ]);

        ordered.Select(p => p.ProjectId).ShouldBe(["dna-entropy-aaaaaaaa", "dna-entropy-bbbbbbbb", "alpha-1", "zeta-1"]);
    }

    [Fact]
    public void Ordering_is_case_insensitive_on_the_name_and_empty_in_empty_out()
    {
        ProjectCatalogOrder.Prefer([P("b", "beta", false), P("a", "Alpha", false)]).Select(p => p.ProjectId).ShouldBe(["a", "b"]);
        ProjectCatalogOrder.Prefer([]).ShouldBeEmpty();
    }
}
