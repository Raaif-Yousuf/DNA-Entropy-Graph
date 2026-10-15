using DnaEntropyGraph.Core;
using DnaEntropyGraph.Presentation.Services;
using DnaEntropyGraph.Presentation.ViewModels;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

/// <summary>Issue #108: the Settings switch that adds or removes the "Open with" entries, and what it says when Windows refuses.</summary>
public class FileAssociationSettingsTests
{
    private readonly IClassesRegistry _registry = Substitute.For<IClassesRegistry>();
    private readonly IStringResourceProvider _strings = Substitute.For<IStringResourceProvider>();

    public FileAssociationSettingsTests() => _strings.GetString(Arg.Any<string>()).Returns(call => call.Arg<string>());

    private FileAssociationSettingsViewModel NewViewModel(string? exe = @"C:\app\app.exe")
        => new(new FileAssociationService(_registry, () => exe), _strings);

    [Fact]
    public void The_switch_shows_what_is_registered_now_and_opening_the_page_writes_nothing()
    {
        _registry.ValueExists(Arg.Any<string>(), Arg.Any<string?>()).Returns(true);

        var viewModel = NewViewModel();

        viewModel.OpenWithEnabled.ShouldBeTrue();
        _registry.DidNotReceiveWithAnyArgs().SetValue(default!, default, default!);
        _registry.DidNotReceiveWithAnyArgs().DeleteSubKeyTree(default!);
    }

    [Fact]
    public void Turning_it_on_registers_the_open_with_entries_and_turning_it_off_removes_them()
    {
        var viewModel = NewViewModel();

        viewModel.OpenWithEnabled = true;
        _registry.Received().SetValue(Arg.Is<string>(k => k.EndsWith(@"\shell\open\command", StringComparison.Ordinal)), Arg.Is<string?>(n => n == null), "\"C:\\app\\app.exe\" \"%1\"");

        viewModel.OpenWithEnabled = false;
        _registry.Received(1).DeleteSubKeyTree(Arg.Is<string>(k => k.EndsWith(FileAssociations.ProgId, StringComparison.Ordinal)));
        viewModel.OpenWithStatus.ShouldBeEmpty();
    }

    [Fact]
    public void A_registry_that_refuses_puts_the_switch_back_and_says_what_to_do()
    {
        _registry.When(r => r.SetValue(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>())).Do(_ => throw new UnauthorizedAccessException());
        var viewModel = NewViewModel();

        viewModel.OpenWithEnabled = true;

        viewModel.OpenWithEnabled.ShouldBeFalse();
        viewModel.OpenWithStatus.ShouldBe("OpenWithNotSaved");
        viewModel.HasOpenWithStatus.ShouldBeTrue();
    }
}
