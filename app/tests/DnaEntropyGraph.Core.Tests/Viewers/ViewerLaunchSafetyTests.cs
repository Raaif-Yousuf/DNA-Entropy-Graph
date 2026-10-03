using DnaEntropyGraph.Core.Viewers;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests.Viewers;

/// <summary>
/// Issue #586: a .bat or .cmd target is run through cmd.exe, which reads &amp; | ^ and friends in an argument as commands,
/// and <c>ProcessStartInfo.ArgumentList</c> does not escape them. Such a launch is refused; an .exe takes any argument.
/// </summary>
public sealed class ViewerLaunchSafetyTests
{
    private const string Metacharacters = "&|^%<>()!\"";

    [Theory]
    [InlineData(@"C:\IGV\igv.bat")]
    [InlineData(@"C:\IGV\igv.CMD")]
    public void A_batch_file_with_an_ampersand_in_an_argument_is_refused(string program)
    {
        ViewerLaunchSafety.RefusesArguments(program, [@"C:\Lab\R&D\a.entropy.bedgraph"]).ShouldBeTrue();
        ViewerLaunchSafety.RefusesArguments(program, [@"C:\Lab\x&calc\a.fasta"]).ShouldBeTrue();
    }

    [Fact]
    public void Every_cmd_metacharacter_is_refused_for_a_batch_file()
    {
        Metacharacters.Length.ShouldBe(10);
        foreach (var c in Metacharacters)
        {
            ViewerLaunchSafety.RefusesArguments(@"C:\IGV\igv.bat", ["C:\\Lab\\a" + c + "b\\a.fasta"]).ShouldBeTrue($"'{c}' must be refused");
        }
    }

    [Fact]
    public void A_plain_path_is_allowed_for_a_batch_file()
    {
        ViewerLaunchSafety.RefusesArguments(@"C:\IGV\igv.bat", [@"C:\My Lab\run 1\a.entropy.bedgraph", "-g", @"C:\My Lab\a.fasta"]).ShouldBeFalse();
    }

    [Fact]
    public void An_exe_takes_any_argument()
    {
        foreach (var c in Metacharacters)
        {
            ViewerLaunchSafety.RefusesArguments(@"C:\IGV\igv.exe", ["C:\\Lab\\a" + c + "b\\a.fasta"]).ShouldBeFalse($"'{c}' is fine for an exe");
        }
    }
}
