using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// Issue #263 review: the csproj stamps the short git sha into the version. <c>ConsoleToMSBuild</c> captures stderr too, so a failing git
/// ("dubious ownership") used to become the sha. MEASURED 2026-10-15: with GIT_DIR=/nonexistent the target now yields "unknown", and with a
/// working checkout the 7 hex digits. This pins the three parts of that behaviour in the file itself.
/// </summary>
public class GitRevisionStampGuardTests
{
    private static string Csproj() => File.ReadAllText(Path.Combine(RepoPaths.AppRoot, "src", "DnaEntropyGraph.App", "DnaEntropyGraph.App.csproj"));

    [Fact]
    public void Git_stderr_is_sent_to_the_null_device_so_it_can_never_become_the_revision()
    {
        Csproj().ShouldContain("rev-parse --short=7 HEAD 2&gt;$(_DegNullDevice)");
    }

    [Fact]
    public void Only_a_hex_revision_is_used_and_anything_else_stamps_unknown()
    {
        var text = Csproj();

        text.ShouldContain("^[0-9a-f]{7,40}$");
        text.ShouldContain(">unknown</SourceRevisionId>");
        text.ShouldNotContain("PropertyName=\"SourceRevisionId\"", customMessage: "the exec output must go through the hex check, not straight into the property");
    }

    [Fact]
    public void The_project_file_ends_with_a_newline()
    {
        Csproj().ShouldEndWith("\n");
    }
}
