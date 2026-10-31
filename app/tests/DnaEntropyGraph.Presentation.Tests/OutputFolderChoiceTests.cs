using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Presentation.Services;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

/// <summary>Issue #954: an output folder that is a whole drive is refused up front, so a run never lands where History cannot delete or re-download it.</summary>
public sealed class OutputFolderChoiceTests
{
    private readonly IStringResourceProvider _strings = Substitute.For<IStringResourceProvider>();

    public OutputFolderChoiceTests() => _strings.GetString(Arg.Any<string>()).Returns(ci => ci.Arg<string>());

    [Theory]
    [InlineData(@"D:\")]
    [InlineData(@"d:\")]
    [InlineData(@"C:\")]
    [InlineData(@"\\?\D:\")]
    public void A_drive_root_is_refused_with_the_plain_message_that_names_the_action(string path)
    {
        var verdict = OutputFolderChoice.Check(path, _strings);

        verdict.Accepted.ShouldBeFalse();
        verdict.Message.ShouldBe(OutputFolderChoice.DriveRootMessage);
    }

    [Theory]
    [InlineData(@"D:\Lab")]
    [InlineData(@"D:\Lab\")]
    [InlineData(@"C:\Users\bob\Downloads")]
    [InlineData(@"\\nas\share")]
    [InlineData(@"\\nas\share\runs")]
    public void A_folder_inside_a_drive_or_a_network_share_is_accepted(string path)
    {
        var verdict = OutputFolderChoice.Check(path, _strings);

        verdict.Accepted.ShouldBeTrue();
        verdict.Message.ShouldBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_folder_means_the_default_and_is_accepted(string? path)
        => OutputFolderChoice.Check(path, _strings).Accepted.ShouldBeTrue();

    [Fact]
    public void A_path_that_cannot_be_resolved_is_left_to_the_run_which_reports_it_not_refused_here()
        => OutputFolderChoice.Check("D:\\bad\0name", _strings).Accepted.ShouldBeTrue();

    [Fact]
    public void The_refusal_reads_its_text_from_the_resource_key()
    {
        _strings.GetString(OutputFolderChoice.DriveRootMessage).Returns("Choose a folder inside the drive, such as D:\\Results.");

        OutputFolderChoice.Check(@"D:\", _strings).Message.ShouldBe("Choose a folder inside the drive, such as D:\\Results.");
    }
}
