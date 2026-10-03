using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>
/// The output-path rule of <see cref="RunTransfer"/>, alone: a result path is text from an object in a bucket and is
/// never trusted as a local path (Hard Rule 14). The runner-level traversal test lives in CloudJobRunnerTransferTests.
/// </summary>
public class RunTransferTests
{
    public static TheoryData<string> BadPaths => new()
    {
        "input/seq.gb",
        "output",
        "../output/x",
        "output/../x",
        "output/./x",
        "output//x",
        "output/",
        "output/a\\b",
        "output/C:/x",
        "/output/x",
        "output/a\u0000b",
    };

    [Theory]
    [MemberData(nameof(BadPaths))]
    public void A_path_that_is_not_a_plain_output_path_is_refused_with_the_worker_failed_code(string path)
    {
        var ex = Should.Throw<RunFailureException>(() => RunTransfer.SafeRelativeOutputPath(path, "Result file 1 of input a"));

        ex.Code.ShouldBe(RunErrorCodes.WorkerFailed);
        ex.Message.ShouldContain("Result file 1 of input a");
    }

    [Fact]
    public void The_bad_path_data_is_not_vacuous()
        => BadPaths.Count().ShouldBeGreaterThan(5);

    [Theory]
    [InlineData("output/track.bedgraph", "track.bedgraph")]
    [InlineData("output/sub/dir/x.csv", "sub|dir|x.csv")]
    public void A_plain_relative_path_is_returned_with_the_platform_separator(string path, string expectedWithPipes)
    {
        var expected = expectedWithPipes.Replace('|', Path.DirectorySeparatorChar);

        RunTransfer.SafeRelativeOutputPath(path, "label").ShouldBe(expected);
    }
}
