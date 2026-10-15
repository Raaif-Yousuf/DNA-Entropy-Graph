using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Presentation.Services;
using DnaEntropyGraph.Presentation.ViewModels;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

/// <summary>Issue #642: the Settings Files group shows where the app keeps its data and opens those folders, and deletes nothing.</summary>
public sealed class FilesSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "deg-files-settings", Guid.NewGuid().ToString("N"));
    private readonly IFolderLauncher _launcher = Substitute.For<IFolderLauncher>();
    private readonly IStringResourceProvider _strings = Substitute.For<IStringResourceProvider>();

    public FilesSettingsTests()
    {
        Directory.CreateDirectory(_root);
        _strings.GetString(Arg.Any<string>()).Returns(call => call.Arg<string>());
        _launcher.OpenFolder(Arg.Any<string>()).Returns(true);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private AppDataRoot Root => AppDataRoot.FromPath(_root);

    private FilesSettingsViewModel Create() => new(Root, _launcher, _strings, new InlineDispatcher());

    private void SaveInput(string job, string name, int bytes)
    {
        var dir = Path.Combine(Root.RunsDirectory, job, "input");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name), new byte[bytes]);
    }

    [Fact]
    public void The_group_shows_the_resolved_root_including_an_override_and_the_two_folders_under_it()
    {
        var vm = Create();

        vm.DataFolderPath.ShouldBe(Path.GetFullPath(_root));
        vm.InputsFolderPath.ShouldBe(Root.RunsDirectory);
        vm.LogsFolderPath.ShouldBe(Root.LogsDirectory);
    }

    [Fact]
    public async Task The_inputs_cache_size_is_the_sum_of_every_saved_input_copy_and_nothing_else()
    {
        SaveInput("job-a", "a.fasta", 1500);
        SaveInput("job-b", "b.gb", 2500);
        var elsewhere = Path.Combine(Root.RunsDirectory, "job-a", "logs");
        Directory.CreateDirectory(elsewhere);
        File.WriteAllBytes(Path.Combine(elsewhere, "worker.log"), new byte[100000]);

        var vm = Create();
        await vm.Ready;

        vm.InputsCacheBytes.ShouldBe(4000);
        vm.InputsCacheSizeText.ShouldBe("3.9 KB");
    }

    [Fact]
    public async Task No_saved_inputs_reads_as_zero_not_as_an_error()
    {
        var vm = Create();
        await vm.Ready;

        vm.InputsCacheBytes.ShouldBe(0);
        vm.InputsCacheSizeText.ShouldBe("0 KB");
    }

    [Theory]
    [InlineData(0L, "0 KB")]
    [InlineData(1L, "1 KB")]
    [InlineData(1024L, "1 KB")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(5L * 1024 * 1024, "5 MB")]
    [InlineData(3L * 1024 * 1024 * 1024, "3 GB")]
    public void Sizes_read_in_plain_units(long bytes, string expected) => FilesSettingsViewModel.FormatSize(bytes).ShouldBe(expected);

    [Fact]
    public void Each_Open_folder_button_opens_its_own_folder_and_only_that_one()
    {
        var vm = Create();

        vm.OpenDataFolderCommand.Execute(null);
        vm.OpenInputsFolderCommand.Execute(null);
        vm.OpenLogsFolderCommand.Execute(null);

        Received.InOrder(() =>
        {
            _launcher.OpenFolder(Path.GetFullPath(_root));
            _launcher.OpenFolder(Root.RunsDirectory);
            _launcher.OpenFolder(Root.LogsDirectory);
        });
    }

    [Fact]
    public void A_folder_Windows_cannot_open_is_reported_with_the_action_and_a_later_success_clears_it()
    {
        _launcher.OpenFolder(Arg.Any<string>()).Returns(false, true);
        var vm = Create();

        vm.OpenLogsFolderCommand.Execute(null);
        vm.FilesStatus.ShouldBe("FilesOpenFailed");

        vm.OpenLogsFolderCommand.Execute(null);
        vm.FilesStatus.ShouldBeEmpty();
    }

    [Fact]
    public async Task Showing_the_group_leaves_every_file_untouched()
    {
        SaveInput("job-a", "a.fasta", 10);
        var before = Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Order().ToArray();

        var vm = Create();
        await vm.Ready;
        vm.OpenInputsFolderCommand.Execute(null);

        Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Order().ToArray().ShouldBe(before);
    }

    private sealed class InlineDispatcher : IDispatcher
    {
        public void Enqueue(Action action) => action();
    }
}
