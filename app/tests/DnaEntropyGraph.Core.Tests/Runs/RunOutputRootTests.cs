using DnaEntropyGraph.Core.Runs;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests.Runs;

public class RunOutputRootTests
{
    [Theory]
    [InlineData(@"C:\x", @"C:\", true)]
    [InlineData(@"C:\x\y", @"C:\", true)]
    [InlineData(@"C:\", @"C:\", false)]
    [InlineData(@"C:\root\a", @"C:\root", true)]
    [InlineData(@"C:\root\a", @"C:\root\", true)]
    [InlineData(@"C:\root2\x", @"C:\root", false)]
    [InlineData(@"C:\ROOT\a", @"c:\root", true)]
    [InlineData(@"C:\root\..\x", @"C:\root", false)]
    [InlineData(@"D:\x", @"C:\", false)]
    public void IsStrictlyInside_handles_drive_roots_siblings_case_and_climbing(string path, string root, bool expected)
    {
        RunOutputRoot.IsStrictlyInside(path, root).ShouldBe(expected);
    }

    [Theory]
    [InlineData(@"C:\", @"C:\x", true)]
    [InlineData(@"C:\x", @"C:\", true)]
    [InlineData(@"C:\x", @"C:\x", true)]
    [InlineData(@"C:\x", @"C:\y", false)]
    [InlineData(@"C:\root", @"C:\root2", false)]
    public void Overlaps_handles_drive_roots(string a, string b, bool expected)
    {
        RunOutputRoot.Overlaps(a, b).ShouldBe(expected);
    }
}
