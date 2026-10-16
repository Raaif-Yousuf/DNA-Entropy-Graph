using DnaEntropyGraph.Core;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests;

/// <summary>Issue #108: "Open with" entries for sequence files, written for this user only, never as the default program.</summary>
public class FileAssociationTests
{
    // Split in two: one literal holding a user-home path trips scripts/check_user_home_paths.py (CLAUDE.md pitfalls).
    private const string Exe = @"C:\Users" + @"\lab\AppData\Local\DnaEntropyGraph\current\DnaEntropyGraph.App.exe";

    /// <summary>A registry with real key semantics: keys exist on their own (a value can be deleted and leave an empty key), a write creates every parent, and a write can be scripted to fail.</summary>
    private sealed class FakeClasses : IClassesRegistry
    {
        public HashSet<string> Keys { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<(string Key, string Name), string> Values { get; } = [];

        /// <summary>Fail the Nth SetValue call (1-based) to an OpenWithProgids key; 0 never fails.</summary>
        public int FailOpenWithWriteNumber { get; set; }

        private int _openWithWrites;

        private void CreateKey(string path)
        {
            var parts = path.Split('\\');
            for (var i = 1; i <= parts.Length; i++)
            {
                Keys.Add(string.Join('\\', parts.Take(i)));
            }
        }

        public void SetValue(string subKeyPath, string? valueName, string value)
        {
            if (subKeyPath.EndsWith("OpenWithProgids", StringComparison.Ordinal) && ++_openWithWrites == FailOpenWithWriteNumber)
            {
                throw new UnauthorizedAccessException("denied");
            }

            CreateKey(subKeyPath);
            Values[(subKeyPath, valueName ?? string.Empty)] = value;
        }

        public void DeleteSubKeyTree(string subKeyPath)
        {
            foreach (var key in Keys.Where(k => k == subKeyPath || k.StartsWith(subKeyPath + "\\", StringComparison.OrdinalIgnoreCase)).ToList())
            {
                Keys.Remove(key);
            }

            foreach (var key in Values.Keys.Where(k => k.Key == subKeyPath || k.Key.StartsWith(subKeyPath + "\\", StringComparison.OrdinalIgnoreCase)).ToList())
            {
                Values.Remove(key);
            }
        }

        public void DeleteValue(string subKeyPath, string valueName) => Values.Remove((subKeyPath, valueName));

        public bool ValueExists(string subKeyPath, string? valueName) => Values.ContainsKey((subKeyPath, valueName ?? string.Empty));

        public bool KeyExists(string subKeyPath) => Keys.Contains(subKeyPath);

        public bool DeleteSubKeyIfEmpty(string subKeyPath)
        {
            var empty = Keys.Contains(subKeyPath)
                && !Values.Keys.Any(k => k.Key == subKeyPath)
                && !Keys.Any(k => k.StartsWith(subKeyPath + "\\", StringComparison.OrdinalIgnoreCase));
            if (empty)
            {
                Keys.Remove(subKeyPath);
            }

            return empty;
        }
    }

    private readonly FakeClasses _registry = new();

    private FileAssociationService Service(string? exe = Exe, bool sandbox = false, bool portable = false) => new(_registry, () => exe, () => sandbox, () => portable);

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
        _registry.Keys.ShouldContain("Software\\Classes\\.gb\\OpenWithProgids", "another program's entry keeps its key");
    }

    [Fact]
    public void Turning_it_off_leaves_no_empty_extension_keys_behind()
    {
        Service().Enable();

        Service().Disable();

        _registry.Keys.ShouldNotContain(k => k.Contains("OpenWithProgids", StringComparison.Ordinal));
        foreach (var extension in FileAssociations.Extensions)
        {
            _registry.Keys.ShouldNotContain($"Software\\Classes\\{extension}");
        }

        _registry.Keys.ShouldNotContain(k => k.Contains(FileAssociations.ProgId, StringComparison.Ordinal));
        _registry.Keys.ShouldContain("Software\\Classes", "the parent keys are never ours to remove");
    }

    [Fact]
    public void An_extension_key_that_holds_the_users_own_default_is_kept()
    {
        _registry.SetValue("Software\\Classes\\.fa", null, "SnapGene.fa");
        Service().Enable();

        Service().Disable();

        _registry.Values[("Software\\Classes\\.fa", string.Empty)].ShouldBe("SnapGene.fa");
        _registry.Keys.ShouldContain("Software\\Classes\\.fa");
        _registry.Keys.ShouldNotContain("Software\\Classes\\.fa\\OpenWithProgids");
    }

    [Fact]
    public void A_failure_part_way_through_turning_it_on_leaves_nothing_behind_and_reads_as_off()
    {
        _registry.FailOpenWithWriteNumber = 3;

        Should.Throw<UnauthorizedAccessException>(() => Service().Enable());

        Service().IsEnabled.ShouldBeFalse();
        _registry.Values.ShouldBeEmpty("the program id and the first two extensions were rolled back");
        _registry.Keys.ShouldNotContain(k => k.Contains(FileAssociations.ProgId, StringComparison.Ordinal) || k.Contains("OpenWithProgids", StringComparison.Ordinal));
    }

    [Fact]
    public void A_half_registered_state_does_not_read_as_enabled()
    {
        Service().Enable();
        _registry.DeleteValue($"Software\\Classes\\{FileAssociations.Extensions[^1]}\\OpenWithProgids", FileAssociations.ProgId);

        Service().IsEnabled.ShouldBeFalse();
    }

    [Fact]
    public void Without_a_known_program_path_nothing_is_written()
    {
        var thrown = Should.Throw<FileAssociationRefusedException>(() => Service(exe: null).Enable());

        thrown.Reason.ShouldBe(FileAssociationRefusal.NoProgramPath);
        _registry.Values.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(@"C:\Program Files\dotnet\dotnet.exe")]
    [InlineData(@"C:\tools\app.exe")]
    [InlineData(@"C:\src\app\bin\DnaEntropyGraph.App.dll")]
    public void Anything_but_the_apps_own_exe_is_refused_so_a_dotnet_run_never_registers_dotnet_exe(string path)
    {
        var thrown = Should.Throw<FileAssociationRefusedException>(() => Service(exe: path).Enable());

        thrown.Reason.ShouldBe(FileAssociationRefusal.NotTheAppExecutable);
        _registry.Values.ShouldBeEmpty();
    }

    [Fact]
    public void The_apps_own_exe_is_accepted_whatever_its_letter_case()
    {
        Service(exe: @"c:\apps\DNAENTROPYGRAPH.APP.EXE").Enable();

        Service().IsEnabled.ShouldBeTrue();
        _registry.Values[($"Software\\Classes\\{FileAssociations.ProgId}\\shell\\open\\command", string.Empty)].ShouldBe("\"c:\\apps\\DNAENTROPYGRAPH.APP.EXE\" \"%1\"");
    }

    [Fact]
    public void A_sandbox_profile_neither_adds_nor_removes_the_real_users_entries()
    {
        Service().Enable();
        var before = _registry.Values.ToDictionary(kv => kv.Key, kv => kv.Value);

        Should.Throw<FileAssociationRefusedException>(() => Service(sandbox: true).Disable()).Reason.ShouldBe(FileAssociationRefusal.SandboxProfile);
        Should.Throw<FileAssociationRefusedException>(() => Service(sandbox: true).Enable()).Reason.ShouldBe(FileAssociationRefusal.SandboxProfile);

        _registry.Values.ShouldBe(before);
    }

    [Theory]
    [InlineData("\"C:\\data\\a.gb\"", 1)]
    [InlineData("C:\\data\\a.gb C:\\data\\b.fasta", 2)]
    [InlineData("\"C:\\my data\\a.fa\" \"D:\\x\\b.GBK\"", 2)]
    [InlineData("\"C:\\a;b\\c;d.gb\"", 1)]
    [InlineData("Z:\\lab share\\a.gb", 0)]
    [InlineData("\"Z:\\lab share\\a.gb\"", 1)]
    public void A_launch_with_sequence_files_finds_them(string arguments, int count)
    {
        // Z: may be a drive the user mapped: allowed on purpose (see AppDeepLink.IsLocalSequenceFile).
        AppDeepLink.TryGetInputFiles(arguments, out var paths).ShouldBe(count > 0);

        paths.Count.ShouldBe(count);
        paths.ShouldAllBe(p => Path.IsPathFullyQualified(p));
    }

    [Fact]
    public void A_path_with_a_semicolon_is_kept_whole()
    {
        AppDeepLink.TryGetInputFiles("\"C:\\a;b\\c;d.gb\"", out var paths).ShouldBeTrue();

        paths.ShouldBe([@"C:\a;b\c;d.gb"]);
    }

    [Fact]
    public void A_path_cannot_pose_as_a_run_link_by_containing_one()
    {
        // A folder named "x;run=job1;y" must not turn a file launch into "open run job1".
        AppDeepLink.TryGetRunId("\"C:\\x;run=job1;y\\f.gb\"", out var jobId).ShouldBeFalse();
        jobId.ShouldBeEmpty();
    }

    [Fact]
    public void A_run_link_and_a_quoted_file_in_one_launch_are_both_found()
    {
        AppDeepLink.TryGetRunId("run=job-3;\"C:\\a;b\\f.gb\"", out var jobId).ShouldBeTrue();
        jobId.ShouldBe("job-3");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("run=job-1")]
    [InlineData("relative\\a.gb")]
    [InlineData("C:\\data\\a.exe")]
    [InlineData("C:\\data\\*.gb")]
    [InlineData("\\\\server\\share\\a.gb")]
    [InlineData("//server/share/a.gb")]
    [InlineData("\\/server/share/a.gb")]
    [InlineData("/\\server\\share\\a.gb")]
    [InlineData("\\\\?\\UNC\\server\\share\\a.gb")]
    [InlineData("//?/UNC/server/share/a.gb")]
    [InlineData("\\\\?\\C:\\data\\a.gb")]
    [InlineData("\\\\.\\C:\\data\\a.gb")]
    [InlineData("\"//server/share/a.gb\"")]
    public void Anything_that_is_not_a_full_path_to_a_sequence_file_is_ignored(string? arguments)
    {
        // The text arrives from another process, so it is never trusted to be a path to a sequence file.
        AppDeepLink.TryGetInputFiles(arguments, out var paths).ShouldBeFalse();
        paths.ShouldBeEmpty();
    }

    [Fact]
    public void Portable_mode_refuses_both_directions_and_writes_nothing_to_the_registry()
    {
        var before = _registry.Values.ToArray();

        Should.Throw<FileAssociationRefusedException>(() => Service(portable: true).Enable()).Reason.ShouldBe(FileAssociationRefusal.PortableMode);
        Should.Throw<FileAssociationRefusedException>(() => Service(portable: true).Disable()).Reason.ShouldBe(FileAssociationRefusal.PortableMode);
        _registry.Values.ToArray().ShouldBe(before);
    }
}
