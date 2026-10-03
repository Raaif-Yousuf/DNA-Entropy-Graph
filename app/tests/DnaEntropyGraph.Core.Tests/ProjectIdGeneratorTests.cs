using System.Text.RegularExpressions;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests;

/// <summary>Issue #50: the id of a project the app creates is <c>dna-entropy-</c> plus eight random characters, and always a legal Google project id.</summary>
public class ProjectIdGeneratorTests
{
    // Google: 6 to 30 characters, lowercase letters, digits and hyphens, starting with a letter, not ending in a hyphen.
    private static readonly Regex GoogleProjectId = new("^[a-z][a-z0-9-]{4,28}[a-z0-9]$");

    [Fact]
    public void Generated_id_is_the_prefix_plus_eight_lowercase_alphanumerics()
    {
        var id = ProjectIdGenerator.Generate();

        id.ShouldMatch("^dna-entropy-[a-z0-9]{8}$");
        GoogleProjectId.IsMatch(id).ShouldBeTrue();
        ProjectIdGenerator.Prefix.ShouldBe("dna-entropy-");
    }

    [Fact]
    public void Many_generated_ids_are_all_legal_and_do_not_collide()
    {
        var ids = Enumerable.Range(0, 2000).Select(_ => ProjectIdGenerator.Generate()).ToList();

        ids.Count.ShouldBe(2000);
        ids.ShouldAllBe(id => GoogleProjectId.IsMatch(id));
        ids.Distinct().Count().ShouldBe(2000);
    }

    [Fact]
    public void A_seeded_source_gives_a_reproducible_id()
    {
        ProjectIdGenerator.Generate(new Random(7)).ShouldBe(ProjectIdGenerator.Generate(new Random(7)));
        ProjectIdGenerator.Generate(new Random(7)).ShouldNotBe(ProjectIdGenerator.Generate(new Random(8)));
    }

    [Fact]
    public void The_display_name_is_readable_and_within_googles_limit()
    {
        ProjectIdGenerator.DisplayName.ShouldBe("DNA Entropy Graph");
        ProjectIdGenerator.DisplayName.Length.ShouldBeInRange(4, 30);
    }

    [Theory]
    [InlineData("dna-entropy-abcd1234", true)]
    [InlineData("my-lab", true)]
    [InlineData("a/b", false)]
    [InlineData("a/bcdef", false)]
    [InlineData("../projects", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("Capital1", false)]
    [InlineData("short", false)]
    [InlineData("ends-with-", false)]
    [InlineData("1abcdef", false)]
    [InlineData("abcdefghijklmnopqrstuvwxyz12345", false)]
    [InlineData("abcdefghijklmnopqrstuvwxyz1234", true)]
    public void IsValid_follows_googles_project_id_grammar(string? id, bool expected)
    {
        ProjectIdGenerator.IsValid(id).ShouldBe(expected);
    }

    [Fact]
    public void Every_generated_id_is_valid()
    {
        Enumerable.Range(0, 500).ShouldAllBe(_ => ProjectIdGenerator.IsValid(ProjectIdGenerator.Generate()));
    }
}
