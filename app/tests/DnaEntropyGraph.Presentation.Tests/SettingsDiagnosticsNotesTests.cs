using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Diagnostics;
using DnaEntropyGraph.Presentation.Services;
using DnaEntropyGraph.Presentation.ViewModels;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

/// <summary>Issue #141: the "include my notes and tags" box beside Save diagnostics. Off unless the user ticks it, every time.</summary>
public sealed class SettingsDiagnosticsNotesTests
{
    private readonly IDiagnosticsExporter _exporter = Substitute.For<IDiagnosticsExporter>();
    private readonly IFilePicker _picker = Substitute.For<IFilePicker>();
    private readonly SettingsViewModel _viewModel;

    public SettingsDiagnosticsNotesTests()
    {
        var strings = Substitute.For<IStringResourceProvider>();
        strings.GetString(Arg.Any<string>()).Returns(call => call.Arg<string>() + "|{0}");
        _picker.PickSaveZipAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("C:\\out\\d.zip");
        _viewModel = new SettingsViewModel(
            Substitute.For<ISettingsStore>(), Substitute.For<IToastService>(), strings, _exporter, _picker, Substitute.For<IFolderLauncher>(),
            TimeProvider.System, Substitute.For<IThemeApplier>());
    }

    [Fact]
    public async Task The_box_is_off_by_default_and_the_export_is_asked_to_leave_notes_and_tags_out()
    {
        _viewModel.IncludeNotesAndTags.ShouldBeFalse();

        await _viewModel.SaveDiagnosticsCommand.ExecuteAsync(null);

        await _exporter.Received(1).ExportAsync("C:\\out\\d.zip", Arg.Any<CancellationToken>(), false);
    }

    [Fact]
    public async Task Ticking_the_box_asks_the_export_to_include_them()
    {
        _viewModel.IncludeNotesAndTags = true;

        await _viewModel.SaveDiagnosticsCommand.ExecuteAsync(null);

        await _exporter.Received(1).ExportAsync("C:\\out\\d.zip", Arg.Any<CancellationToken>(), true);
    }

    [Fact]
    public async Task The_box_unticks_itself_after_a_save_so_the_next_bundle_is_private_again()
    {
        _viewModel.IncludeNotesAndTags = true;
        await _viewModel.SaveDiagnosticsCommand.ExecuteAsync(null);

        _viewModel.IncludeNotesAndTags.ShouldBeFalse();
    }
}
