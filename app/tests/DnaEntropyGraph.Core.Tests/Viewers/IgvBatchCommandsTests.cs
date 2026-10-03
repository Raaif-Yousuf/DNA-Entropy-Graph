using DnaEntropyGraph.Core.Viewers;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests.Viewers;

public sealed class IgvBatchCommandsTests
{
    [Fact]
    public void Commands_are_new_then_genome_then_bedgraphs_then_gff3s_with_every_path_quoted()
    {
        var files = new IgvFiles(@"C:\my out\a.fasta", [@"C:\my out\a.entropy.bedgraph"], [@"C:\my out\a.genes.gff3"]);

        IgvBatchCommands.Build(files).ShouldBe(
        [
            "new",
            "genome \"C:\\my out\\a.fasta\"",
            "load \"C:\\my out\\a.entropy.bedgraph\"",
            "load \"C:\\my out\\a.genes.gff3\"",
        ]);
    }

    [Fact]
    public void No_genome_means_no_genome_command()
    {
        IgvBatchCommands.Build(new IgvFiles(null, [@"C:\a.bedgraph"], [])).ShouldBe(["new", "load \"C:\\a.bedgraph\""]);
    }

    [Fact]
    public void A_path_with_a_line_break_cannot_smuggle_in_a_second_command()
    {
        var files = new IgvFiles("C:\\a\nexit", [], []);

        Should.Throw<ArgumentException>(() => IgvBatchCommands.Build(files));
    }

    [Fact]
    public void Launch_arguments_name_the_genome_and_join_the_tracks_with_commas()
    {
        var files = new IgvFiles(@"C:\a.fasta", [@"C:\a.bedgraph"], [@"C:\a.gff3"]);

        IgvBatchCommands.LaunchArguments(files).ShouldBe([@"C:\a.bedgraph,C:\a.gff3", "-g", @"C:\a.fasta"]);
    }

    [Fact]
    public void Launch_arguments_leave_out_a_track_whose_path_has_a_comma_because_igv_splits_on_it()
    {
        var files = new IgvFiles(null, [@"C:\a,b.bedgraph", @"C:\ok.bedgraph"], []);

        IgvBatchCommands.LaunchArguments(files).ShouldBe([@"C:\ok.bedgraph"]);
    }
}
