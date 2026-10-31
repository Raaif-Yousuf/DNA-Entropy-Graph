using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests;

/// <summary>Issue #830 stage 3: the row's own project and account are the scope everything that acts on the run uses.</summary>
public sealed class RunScopeTests
{
    private static readonly RunRecord Row = new("job-1", JobPhase.Completed, DateTimeOffset.UnixEpoch, ProjectId: "proj-a", AccountSub: "sub-a");

    [Fact]
    public void A_row_becomes_its_projects_and_its_accounts_scope()
        => RunScope.Of(Row).ShouldBe(new CloudScope("proj-a", "sub-a"));

    [Fact]
    public void A_row_with_no_recorded_account_is_the_current_account_and_a_row_with_no_project_has_an_empty_one()
    {
        RunScope.Of(Row with { AccountSub = null }).ShouldBe(new CloudScope("proj-a"));
        RunScope.Of(Row with { ProjectId = null }).ProjectId.ShouldBeEmpty();
    }

    [Fact]
    public void The_account_is_never_printed_with_the_row()
    {
        var text = Row.ToString();

        text.ShouldNotContain("sub-a");
        text.ShouldContain("<account withheld>");
    }
}
