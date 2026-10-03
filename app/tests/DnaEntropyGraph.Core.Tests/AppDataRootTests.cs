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
}
