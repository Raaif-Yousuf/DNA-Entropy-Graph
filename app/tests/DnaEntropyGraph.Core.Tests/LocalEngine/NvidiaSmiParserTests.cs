using DnaEntropyGraph.Core.LocalEngine;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests.LocalEngine;

public sealed class NvidiaSmiParserTests
{
    [Fact]
    public void One_gpu_line_gives_name_memory_and_driver()
    {
        var gpus = NvidiaSmiParser.Parse("NVIDIA GeForce RTX 4090, 24564, 551.61\r\n");

        gpus.Count.ShouldBe(1);
        gpus[0].Name.ShouldBe("NVIDIA GeForce RTX 4090");
        gpus[0].MemoryMiB.ShouldBe(24564);
        gpus[0].DriverVersion.ShouldBe("551.61");
    }

    [Fact]
    public void Two_gpus_give_two_entries_in_order()
    {
        var gpus = NvidiaSmiParser.Parse("NVIDIA RTX A4000, 16376, 550.90.07\nNVIDIA L4, 23034, 550.90.07\n");

        gpus.Select(g => g.Name).ShouldBe(["NVIDIA RTX A4000", "NVIDIA L4"]);
    }

    [Fact]
    public void A_memory_value_with_a_unit_suffix_is_still_read()
    {
        NvidiaSmiParser.Parse("NVIDIA L4, 23034 MiB, 550.90")[0].MemoryMiB.ShouldBe(23034);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n")]
    [InlineData("NVIDIA-SMI has failed because it couldn't communicate with the NVIDIA driver.")]
    [InlineData("Name, abc, 551.61")]
    [InlineData("Only one field")]
    public void Unusable_output_gives_no_gpus(string text)
    {
        NvidiaSmiParser.Parse(text).ShouldBeEmpty();
    }

    [Fact]
    public void A_bad_line_between_good_ones_is_skipped()
    {
        NvidiaSmiParser.Parse("NVIDIA L4, 23034, 550.90\ngarbage\nNVIDIA L4, 23034, 550.90").Count.ShouldBe(2);
    }
}
