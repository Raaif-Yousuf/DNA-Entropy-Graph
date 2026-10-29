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
    [InlineData(@"\\server\share", false)]
    [InlineData(@"\\server\share\", false)]
    [InlineData(@"\\server\share\x", false)]
    [InlineData(@"C:\x", false)]
    [InlineData(@"\\?\C:\", true)]
    [InlineData(@"\\.\C:\", true)]
    [InlineData(@"\\?\C:\x", false)]
    [InlineData(@"\\?\UNC\server\share", false)]
    [InlineData(@"\\.\UNC\server\share\x", false)]
    [InlineData("Q:", true)]
    public void IsDriveRoot_names_drive_roots_and_not_share_roots(string path, bool expected)
    {
        RunOutputRoot.IsDriveRoot(path).ShouldBe(expected);
    }

    [Theory]
    [InlineData(@"C:\Windows\Temp\x", @"C:\")]
    public void IsSafelyInside_never_treats_a_drive_root_as_the_output_root(string path, string root)
    {
        RunOutputRoot.IsStrictlyInside(path, root).ShouldBeTrue("the read-only containment answer stays correct");
        RunOutputRoot.IsSafelyInside(path, root).ShouldBeFalse();
    }

    [Theory]
    [InlineData(@"\\nas\labshare\sample", @"\\nas\labshare")]
    [InlineData(@"\\nas\labshare\sample", @"\\nas\labshare\")]
    public void IsSafelyInside_still_accepts_a_share_root_as_the_output_root(string path, string root)
    {
        // A lab NAS share is a normal output folder; only a drive root is refused (it was refused before the drive-root fix too).
        RunOutputRoot.IsSafelyInside(path, root).ShouldBeTrue();
        RunOutputRoot.IsSafelyInside(@"\\nas\labshare2\x", root).ShouldBeFalse();
    }

    [Theory]
    [InlineData("job-1", true)]
    [InlineData("0e9d8a52-7f3b", true)]
    [InlineData("", false)]
    [InlineData(" ", false)]
    [InlineData(".", false)]
    [InlineData("..", false)]
    [InlineData(@"x\..\y", false)]
    [InlineData("x/../y", false)]
    [InlineData(@"a\b", false)]
    [InlineData("a/b", false)]
    [InlineData("C:x", false)]
    [InlineData("a:stream", false)]
    [InlineData("a\0b", false)]
    [InlineData("a..b", false)]
    public void IsPlainJobId_accepts_one_plain_folder_name_only(string jobId, bool expected)
    {
        RunOutputRoot.IsPlainJobId(jobId).ShouldBe(expected);
    }

    [Fact]
    public void IsSafelyInside_still_accepts_an_ordinary_subfolder_root()
    {
        RunOutputRoot.IsSafelyInside(@"C:\out\run", @"C:\out").ShouldBeTrue();
        RunOutputRoot.IsSafelyInside(@"C:\out2\run", @"C:\out").ShouldBeFalse();
    }

    [Fact]
    public void IsContained_rejects_a_drive_root_and_a_junction_but_accepts_a_real_folder()
    {
        var root = Path.Combine(Path.GetTempPath(), "rog-" + Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(Path.GetTempPath(), "rog-out-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "real"));
        Directory.CreateDirectory(outside);
        try
        {
            RunOutputRoot.IsContained(Path.Combine(root, "real"), root).ShouldBeTrue();
            RunOutputRoot.IsContained(@"C:\Windows\Temp\x", @"C:\").ShouldBeFalse("main's drive-root rule (IsSafelyInside)");

            var link = Path.Combine(root, "link");
            Directory.CreateSymbolicLink(link, outside);
            RunOutputRoot.IsContained(link, root).ShouldBeFalse("the #588 reparse-point rule (NoReparseBetween)");
        }
        catch (UnauthorizedAccessException)
        {
            // Creating a symlink needs developer mode or elevation; the junction arm is then covered by the Cloud.Tests streamed-download tests.
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(outside, true);
        }
    }

    [Theory]
    [InlineData(@"Q:\stem", @"Q:\", true)]
    [InlineData(@"Q:\a\b", @"Q:\", true)]
    [InlineData(@"Q:\", @"Q:\", false)]
    [InlineData(@"R:\stem", @"Q:\", false)]
    public void IsUnderRootWithoutLinks_accepts_a_drive_root_as_the_output_folder_for_resume(string path, string root, bool expected)
    {
        // A resumed run reuses its recorded folder, and a user who picked a whole drive must keep that; only History-row deletes refuse a drive root.
        RunOutputRoot.IsUnderRootWithoutLinks(path, root).ShouldBe(expected);
        if (expected)
        {
            RunOutputRoot.IsContained(path, root).ShouldBeFalse("the History-row gate still refuses a drive root");
        }
    }
}