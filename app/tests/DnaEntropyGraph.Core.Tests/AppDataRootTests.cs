using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests;

/// <summary>Issue #638: one resolved data root (command line, then DEG_DATA_DIR, then the default) that every piece of app state hangs off.</summary>
public sealed class AppDataRootTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "deg-root-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    private static Func<string, string?> NoEnv => _ => null;

    private static Func<string, string?> Env(string value) => name => name == AppDataRoot.EnvironmentVariable ? value : null;

    [Fact]
    public void With_no_override_the_root_is_the_default_and_nothing_is_created()
    {
        var root = AppDataRoot.Resolve([], NoEnv, _scratch);

        root.IsOverride.ShouldBeFalse();
        root.Path.ShouldBe(AppDataRoot.Default().Path);
        root.Path.ShouldEndWith("DNAEntropyGraph");
    }

    [Fact]
    public void The_environment_variable_redirects_the_root_and_creates_a_missing_folder()
    {
        var target = Path.Combine(_scratch, "env-profile");

        var root = AppDataRoot.Resolve([], Env(target), _scratch);

        root.IsOverride.ShouldBeTrue();
        root.Path.ShouldBe(target);
        Directory.Exists(target).ShouldBeTrue();
    }

    [Fact]
    public void The_command_line_wins_over_the_environment_variable()
    {
        var fromArgs = Path.Combine(_scratch, "from-args");
        var fromEnv = Path.Combine(_scratch, "from-env");

        var root = AppDataRoot.Resolve(["--profile", fromArgs], Env(fromEnv), _scratch);

        root.Path.ShouldBe(fromArgs);
        Directory.Exists(fromEnv).ShouldBeFalse("the losing source must not be touched");
    }

    [Fact]
    public void The_equals_form_is_accepted_and_other_arguments_are_ignored()
    {
        var target = Path.Combine(_scratch, "eq");

        var root = AppDataRoot.Resolve(["--other", $"--profile={target}", "x"], NoEnv, _scratch);

        root.Path.ShouldBe(target);
    }

    [Fact]
    public void A_relative_path_resolves_against_the_given_current_directory()
    {
        Directory.CreateDirectory(_scratch);

        var root = AppDataRoot.Resolve(["--profile", Path.Combine("sub", "p")], NoEnv, _scratch);

        root.Path.ShouldBe(Path.Combine(_scratch, "sub", "p"));
        Directory.Exists(root.Path).ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_environment_variable_means_no_override(string blank)
    {
        AppDataRoot.Resolve([], Env(blank), _scratch).IsOverride.ShouldBeFalse();
    }

    [Fact]
    public void A_path_that_is_a_file_fails_naming_the_path()
    {
        Directory.CreateDirectory(_scratch);
        var file = Path.Combine(_scratch, "iam.txt");
        File.WriteAllText(file, "x");

        var ex = Should.Throw<DataFolderUnusableException>(() => AppDataRoot.Resolve(["--profile", file], NoEnv, _scratch));

        ex.Path.ShouldBe(file);
        ex.Reason.ShouldBe(DataFolderProblem.NotAFolder);
    }

    [Fact]
    public void The_switch_with_no_value_fails()
    {
        var ex = Should.Throw<DataFolderUnusableException>(() => AppDataRoot.Resolve(["--profile"], NoEnv, _scratch));

        ex.Reason.ShouldBe(DataFolderProblem.MissingValue);
    }

    [Fact]
    public void Every_derived_path_is_under_the_root_and_none_under_the_default_when_overridden()
    {
        var root = AppDataRoot.Resolve(["--profile", Path.Combine(_scratch, "p")], NoEnv, _scratch);
        var defaultRoot = AppDataRoot.Default().Path;

        string[] derived =
        [
            root.SettingsFile, root.InstallationIdFile, root.DatabaseFile, root.AuthDirectory,
            root.LogsDirectory, root.RunsDirectory, root.WebView2Directory,
        ];

        derived.Length.ShouldBe(7);
        foreach (var path in derived)
        {
            path.ShouldStartWith(root.Path + Path.DirectorySeparatorChar);
            path.ShouldNotStartWith(defaultRoot);
        }

        Path.GetFileName(root.SettingsFile).ShouldBe("settings.json");
        Path.GetFileName(root.DatabaseFile).ShouldBe("app.db");
        Path.GetFileName(root.InstallationIdFile).ShouldBe("installation_id");
    }

    [Fact]
    public void A_value_the_system_cannot_normalise_fails_as_an_invalid_path_from_either_source()
    {
        Should.Throw<DataFolderUnusableException>(() => AppDataRoot.Resolve(["--profile", "bad\0name"], NoEnv, _scratch))
            .Reason.ShouldBe(DataFolderProblem.InvalidPath);
        Should.Throw<DataFolderUnusableException>(() => AppDataRoot.Resolve([], Env("bad\0name"), _scratch))
            .Reason.ShouldBe(DataFolderProblem.InvalidPath);
    }

    [Fact]
    public void A_stream_style_path_is_refused_with_the_plain_failure_and_not_an_unhandled_exception()
    {
        // MEASURED 2026-10-03: GetFullPath accepts this; Directory.CreateDirectory is what rejects it.
        Should.Throw<DataFolderUnusableException>(() => AppDataRoot.Resolve(["--profile", "C:\a:b:c"], NoEnv, _scratch));
    }

    [Fact]
    public void A_following_switch_is_a_missing_value_not_the_folder()
    {
        Should.Throw<DataFolderUnusableException>(() => AppDataRoot.Resolve(["--profile", "--foo"], NoEnv, _scratch))
            .Reason.ShouldBe(DataFolderProblem.MissingValue);
        Directory.Exists(Path.Combine(_scratch, "--foo")).ShouldBeFalse();
    }

    [Fact]
    public void A_folder_that_cannot_be_written_to_fails_as_cannot_create_and_a_good_one_is_probed_once()
    {
        var probed = new List<string>();
        var target = Path.Combine(_scratch, "probe-ok");
        AppDataRoot.Resolve(["--profile", target], NoEnv, _scratch, writeProbe: probed.Add);
        probed.ShouldBe([target]);

        var ex = Should.Throw<DataFolderUnusableException>(() => AppDataRoot.Resolve(
            ["--profile", Path.Combine(_scratch, "ro")], NoEnv, _scratch, writeProbe: _ => throw new UnauthorizedAccessException()));
        ex.Reason.ShouldBe(DataFolderProblem.CannotCreate);
    }

    [Fact]
    public void The_real_write_probe_passes_on_a_normal_folder_and_leaves_nothing_behind()
    {
        var root = AppDataRoot.Resolve(["--profile", Path.Combine(_scratch, "real-probe")], NoEnv, _scratch);

        Directory.GetFileSystemEntries(root.Path).ShouldBeEmpty();
    }

    private string MakePortableProgramDir(bool withFlag = true)
    {
        var dir = Path.Combine(_scratch, "program");
        Directory.CreateDirectory(dir);
        if (withFlag)
        {
            File.WriteAllText(Path.Combine(dir, AppDataRoot.PortableFlagFileName), string.Empty);
        }

        return dir;
    }

    [Fact]
    public void A_portable_flag_beside_the_program_puts_all_state_in_a_data_folder_beside_it_and_not_under_the_default()
    {
        var program = MakePortableProgramDir();

        var root = AppDataRoot.Resolve([], NoEnv, _scratch, programDirectory: program);

        root.IsPortable.ShouldBeTrue();
        root.IsOverride.ShouldBeFalse("portable is the real profile for this copy, not a sandbox: no banner, no simulated cloud");
        root.Path.ShouldBe(Path.Combine(program, "data"));
        Directory.Exists(root.Path).ShouldBeTrue();
        root.DatabaseFile.ShouldBe(Path.Combine(program, "data", "app.db"));
        root.Path.ShouldNotStartWith(AppDataRoot.Default().Path);
    }

    [Fact]
    public void Without_the_flag_the_program_directory_is_ignored_and_nothing_is_created()
    {
        var program = MakePortableProgramDir(withFlag: false);

        var root = AppDataRoot.Resolve([], NoEnv, _scratch, programDirectory: program);

        root.IsPortable.ShouldBeFalse();
        root.Path.ShouldBe(AppDataRoot.Default().Path);
        Directory.Exists(Path.Combine(program, "data")).ShouldBeFalse();
    }

    [Fact]
    public void An_explicit_profile_or_environment_override_beats_the_portable_flag()
    {
        var program = MakePortableProgramDir();
        var target = Path.Combine(_scratch, "explicit");

        var fromArgs = AppDataRoot.Resolve(["--profile", target], NoEnv, _scratch, programDirectory: program);
        var fromEnv = AppDataRoot.Resolve([], Env(target), _scratch, programDirectory: program);

        fromArgs.Path.ShouldBe(target);
        fromEnv.Path.ShouldBe(target);
        fromArgs.IsPortable.ShouldBeFalse();
        fromArgs.IsOverride.ShouldBeTrue();
        Directory.Exists(Path.Combine(program, "data")).ShouldBeFalse("the losing source must not be touched");
    }

    [Fact]
    public void A_portable_folder_that_cannot_be_written_fails_naming_it_and_never_falls_back_to_the_real_folder()
    {
        var program = MakePortableProgramDir();

        var ex = Should.Throw<DataFolderUnusableException>(() => AppDataRoot.Resolve(
            [], NoEnv, _scratch, writeProbe: _ => throw new UnauthorizedAccessException(), programDirectory: program));

        ex.Reason.ShouldBe(DataFolderProblem.CannotCreate);
        ex.Path.ShouldBe(Path.Combine(program, "data"));
    }

    [Fact]
    public void A_data_path_that_is_a_file_beside_a_portable_flag_fails_as_not_a_folder()
    {
        var program = MakePortableProgramDir();
        File.WriteAllText(Path.Combine(program, "data"), "x");

        Should.Throw<DataFolderUnusableException>(() => AppDataRoot.Resolve([], NoEnv, _scratch, programDirectory: program))
            .Reason.ShouldBe(DataFolderProblem.NotAFolder);
    }

    private string MakeVelopackLayout(bool flagAtRoot, bool flagInCurrent = false)
    {
        var root = Path.Combine(_scratch, "install");
        var current = Path.Combine(root, "current");
        Directory.CreateDirectory(current);
        File.WriteAllText(Path.Combine(root, "Update.exe"), string.Empty);
        if (flagAtRoot)
        {
            File.WriteAllText(Path.Combine(root, AppDataRoot.PortableFlagFileName), string.Empty);
        }

        if (flagInCurrent)
        {
            File.WriteAllText(Path.Combine(current, AppDataRoot.PortableFlagFileName), string.Empty);
        }

        return current;
    }

    [Fact]
    public void In_the_Velopack_layout_the_flag_and_the_data_folder_live_beside_Update_exe_so_an_update_cannot_swap_them_away()
    {
        var current = MakeVelopackLayout(flagAtRoot: true);

        var root = AppDataRoot.Resolve([], NoEnv, _scratch, programDirectory: current);

        root.IsPortable.ShouldBeTrue();
        root.Path.ShouldBe(Path.Combine(_scratch, "install", "data"));
        Directory.Exists(Path.Combine(current, "data")).ShouldBeFalse();
        root.PortableBase.ShouldBe(Path.Combine(_scratch, "install"));
    }

    [Fact]
    public void In_the_Velopack_layout_a_flag_only_inside_current_is_ignored_because_an_update_would_delete_it()
    {
        var current = MakeVelopackLayout(flagAtRoot: false, flagInCurrent: true);

        AppDataRoot.Resolve([], NoEnv, _scratch, programDirectory: current).IsPortable.ShouldBeFalse();
    }

    [Fact]
    public void A_folder_named_current_without_Update_exe_beside_it_is_an_ordinary_portable_folder()
    {
        var current = Path.Combine(_scratch, "stick", "current");
        Directory.CreateDirectory(current);
        File.WriteAllText(Path.Combine(current, AppDataRoot.PortableFlagFileName), string.Empty);

        var root = AppDataRoot.Resolve([], NoEnv, _scratch, programDirectory: current);

        root.Path.ShouldBe(Path.Combine(current, "data"));
    }

    [Fact]
    public void Portable_mode_has_a_portable_base_and_an_outputs_folder_beside_data_and_normal_mode_has_neither()
    {
        var program = MakePortableProgramDir();

        var portable = AppDataRoot.Resolve([], NoEnv, _scratch, programDirectory: program);
        var normal = AppDataRoot.Resolve([], NoEnv, _scratch, programDirectory: Path.Combine(_scratch, "no-flag-here"));

        portable.PortableBase.ShouldBe(program);
        portable.PortableOutputsDirectory.ShouldBe(Path.Combine(program, "outputs"));
        normal.PortableBase.ShouldBeNull();
        normal.PortableOutputsDirectory.ShouldBeNull();
    }

    [Fact]
    public void A_portable_failure_says_it_was_portable_and_an_explicit_override_failure_does_not()
    {
        var program = MakePortableProgramDir();
        File.WriteAllText(Path.Combine(program, "data"), "x");

        Should.Throw<DataFolderUnusableException>(() => AppDataRoot.Resolve([], NoEnv, _scratch, programDirectory: program))
            .IsPortable.ShouldBeTrue();
        Should.Throw<DataFolderUnusableException>(() => AppDataRoot.Resolve(["--profile"], NoEnv, _scratch))
            .IsPortable.ShouldBeFalse();
    }

    [Fact]
    public void The_default_output_source_is_the_portable_outputs_folder_in_portable_mode_and_the_given_downloads_otherwise()
    {
        var program = MakePortableProgramDir();

        AppDataRoot.Resolve([], NoEnv, _scratch, programDirectory: program).DefaultOutputSource(() => "DL")().ShouldBe(Path.Combine(program, "outputs"));
        AppDataRoot.Resolve([], NoEnv, _scratch).DefaultOutputSource(() => "DL")().ShouldBe("DL");
    }
}
