using DnaEntropyGraph.Core;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests;

/// <summary>Issue #108: "Open with" entries for sequence files, written for this user only, never as the default program.</summary>
public class FileAssociationTests
{
    private const string Exe = @"C:\Program Files\DNA Entropy Graph\DnaEntropyGraph.App.exe";

    private sealed class FakeClasses : IClassesRegistry
    {
        public Dictionary<(string Key, string Name), string> Values { get; } = [];

        public void SetValue(string subKeyPath, string? valueName, string value) => Values[(subKeyPath, valueName ?? string.Empty)] = value;

        public void DeleteSubKeyTree(string subKeyPath)
        {
            foreach (var key in Values.Keys.Where(k => k.Key == subKeyPath || k.Key.StartsWith(subKeyPath + "\\", StringComparison.Ordinal)).ToList())
            {
                Values.Remove(key);
            }
        }

        public void DeleteValue(string subKeyPath, string valueName) => Values.Remove((subKeyPath, valueName));

        public bool ValueExists(string subKeyPath, string? valueName) => Values.ContainsKey((subKeyPath, valueName ?? string.Empty));
    }

    private readonly FakeClasses _registry = new();

    private FileAssociationService Service(string? exe = Exe) => new(_registry, () => exe);

    [Fact]
    public void Turning_it_on_adds_an_open_with_entry_for_every_sequence_extension_and_never_a_default()
    {
        Service().Enable();

        foreach (var extension in FileAssociations.Extensions)
        {
            _registry.Values.ShouldContainKey(($"Software\\Classes\\{extension}\\OpenWithProgids", FileAssociations.ProgId));
            _registry.Values.ShouldNotContainKey(($"Software\\Classes\\{extension}", string.Empty), "the app must never become the default program for a file type");
        }

        _registry.Values[($"Software\\Classes\\{FileAssociations.ProgId}\\shell\\open\\command", string.Empty)].ShouldBe($"\"{Exe}\" \"%1\"");
        Service().IsEnabled.ShouldBeTrue();
    }

    [Fact]
    public void Turning_it_off_removes_everything_it_added_and_nothing_else()
    {
        _registry.SetValue("Software\\Classes\\.gb\\OpenWithProgids", "Other.Program", string.Empty);
        Service().Enable();

        Service().Disable();

        Service().IsEnabled.ShouldBeFalse();
        _registry.Values.Keys.ShouldAllBe(k => !k.Key.Contains(FileAssociations.ProgId, StringComparison.Ordinal) && k.Name != FileAssociations.ProgId);
        _registry.Values.ShouldContainKey(("Software\\Classes\\.gb\\OpenWithProgids", "Other.Program"));
    }

    [Fact]
    public void Without_a_known_program_path_nothing_is_written()
    {
        Should.Throw<InvalidOperationException>(() => Service(exe: null).Enable());

        _registry.Values.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("\"C:\\data\\a.gb\"", 1)]
    [InlineData("C:\\data\\a.gb C:\\data\\b.fasta", 2)]
    [InlineData("\"C:\\my data\\a.fa\" \"D:\\x\\b.GBK\"", 2)]
    public void A_launch_with_sequence_files_finds_them(string arguments, int count)
    {
        AppDeepLink.TryGetInputFiles(arguments, out var paths).ShouldBeTrue();

        paths.Count.ShouldBe(count);
        paths.ShouldAllBe(p => Path.IsPathFullyQualified(p));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("run=job-1")]
    [InlineData("relative\\a.gb")]
    [InlineData("C:\\data\\a.exe")]
    [InlineData("C:\\data\\*.gb")]
    [InlineData("\\\\server\\share\\a.gb")]
    public void Anything_that_is_not_a_full_path_to_a_sequence_file_is_ignored(string? arguments)
    {
        // The text arrives from another process, so it is never trusted to be a path to a sequence file.
        AppDeepLink.TryGetInputFiles(arguments, out var paths).ShouldBeFalse();
        paths.ShouldBeEmpty();
    }
}
