using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Runs;
using DnaEntropyGraph.Core.Tracks;
using DnaEntropyGraph.Presentation.Services;
using DnaEntropyGraph.Presentation.ViewModels;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

/// <summary>Issue #552: the Results page lists the run's gene tables, read from the run's own folder.</summary>
public sealed class ResultsGeneTablesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "deg-results-genes", Guid.NewGuid().ToString("N"));
    private readonly IGeneTableSource _source = Substitute.For<IGeneTableSource>();
    private readonly IStringResourceProvider _strings = Substitute.For<IStringResourceProvider>();

    public ResultsGeneTablesTests()
    {
        Directory.CreateDirectory(_dir);
        _strings.GetString(Arg.Any<string>()).Returns(ci => ci.Arg<string>());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static GeneRecord Gene(string id, double mean) => new("c1", id, 1, 30, "+", 30, false, false, mean, mean, mean, null, 0);

    private async Task<ResultsViewModel> Load(params GeneTableFile[] tables)
    {
        var repository = Substitute.For<IRunRepository>();
        repository.GetAllAsync(Arg.Any<CancellationToken>()).Returns(_ => new List<RunRecord> { new("job", JobPhase.Completed, DateTimeOffset.UnixEpoch, OutputDir: _dir) });
        _source.Load(_dir).Returns(tables);
        var vm = new ResultsViewModel(repository, new RunOutputReader(), Substitute.For<IShellLauncher>(), Substitute.For<INavigator>(), _strings, Substitute.For<IExternalViewerOpener>(), geneTables: _source);
        await vm.LoadAsync("job", TestContext.Current.CancellationToken);
        return vm;
    }

    [Fact]
    public async Task A_run_with_a_gene_table_lists_it_sortable()
    {
        var vm = await Load(new GeneTableFile("a.genes.tsv", [Gene("hi", 1.9), Gene("lo", 0.2)]));

        vm.HasGeneTables.ShouldBeTrue();
        var table = vm.GeneTables.Single();
        table.Rows.Select(r => r.GeneId).ShouldBe(["hi", "lo"]);
        table.SortByCommand.Execute("mean");
        table.Rows.Select(r => r.GeneId).ShouldBe(["lo", "hi"]);
        vm.GeneNoticeText.ShouldBeEmpty();
    }

    [Fact]
    public async Task Several_tables_are_each_named_by_their_file_but_one_table_is_not()
    {
        (await Load(new GeneTableFile("a.genes.tsv", [Gene("x", 1)]))).GeneTables.Single().HasTitle.ShouldBeFalse();

        var many = await Load(new GeneTableFile("a/a.genes.tsv", [Gene("x", 1)]), new GeneTableFile("b/b.genes.tsv", [Gene("y", 1)]));
        many.GeneTables.Select(t => t.Title).ShouldBe(["a/a.genes.tsv", "b/b.genes.tsv"]);
    }

    [Fact]
    public async Task An_unreadable_table_says_which_file_and_what_to_do_and_the_others_still_show()
    {
        var vm = await Load(new GeneTableFile("bad.genes.tsv", null), new GeneTableFile("ok.genes.tsv", [Gene("x", 1)]));

        vm.GeneTables.Count.ShouldBe(1);
        vm.GeneNoticeText.ShouldBe(ResultsCopy.GenesUnreadable);
        vm.IsGeneNoticeVisible.ShouldBeTrue();
    }

    [Fact]
    public async Task A_run_with_no_gene_table_shows_none_and_no_notice()
    {
        var vm = await Load();

        vm.HasGeneTables.ShouldBeFalse();
        vm.IsGeneNoticeVisible.ShouldBeFalse();
    }

    [Fact]
    public async Task Without_a_source_the_page_still_loads()
    {
        var repository = Substitute.For<IRunRepository>();
        repository.GetAllAsync(Arg.Any<CancellationToken>()).Returns(_ => new List<RunRecord> { new("job", JobPhase.Completed, DateTimeOffset.UnixEpoch, OutputDir: _dir) });
        var vm = new ResultsViewModel(repository, new RunOutputReader(), Substitute.For<IShellLauncher>(), Substitute.For<INavigator>(), _strings, Substitute.For<IExternalViewerOpener>());

        await vm.LoadAsync("job", TestContext.Current.CancellationToken);

        vm.HasGeneTables.ShouldBeFalse();
    }
}
