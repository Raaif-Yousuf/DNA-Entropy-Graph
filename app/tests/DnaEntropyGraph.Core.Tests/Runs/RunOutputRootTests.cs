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

    [Theory]
    [InlineData(@"C:\", true)]
    [InlineData(@"\\server\share", true)]
    [InlineData(@"\\server\share\", true)]
    [InlineData(@"\\server\share\x", false)]
    [InlineData(@"C:\x", false)]
    public void IsVolumeRoot_names_drive_and_share_roots(string path, bool expected)
    {
        RunOutputRoot.IsVolumeRoot(path).ShouldBe(expected);
    }

    [Theory]
    [InlineData(@"C:\Windows\Temp\x", @"C:\")]
    [InlineData(@"\\server\share\x", @"\\server\share")]
    public void IsSafelyInside_never_treats_a_volume_root_as_the_output_root(string path, string root)
    {
        RunOutputRoot.IsStrictlyInside(path, root).ShouldBeTrue("the read-only containment answer stays correct");
        RunOutputRoot.IsSafelyInside(path, root).ShouldBeFalse();
    }

    [Fact]
    public void IsSafelyInside_still_accepts_an_ordinary_subfolder_root()
    {
        RunOutputRoot.IsSafelyInside(@"C:\out\run", @"C:\out").ShouldBeTrue();
        RunOutputRoot.IsSafelyInside(@"C:\out2\run", @"C:\out").ShouldBeFalse();
    }
}
