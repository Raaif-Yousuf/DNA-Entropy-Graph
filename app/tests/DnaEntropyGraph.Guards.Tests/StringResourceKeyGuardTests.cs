using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// Issue #424's regression guard: the status pill title-bar bug (MEASURED -
/// the pill showed the literal text "StatusPillSignedIn.Text" instead of
/// its resolved string) was a dotted key passed to
/// <c>IStringResourceProvider.GetString</c> from C#, which compiles clean
/// and only fails at runtime, silently, via the not-found fallback. See
/// <see cref="StringResourceKeyScanner"/>'s own doc comment for the full
/// "why a code lookup cannot use the dotted x:Uid form" reasoning.
/// </summary>
public class StringResourceKeyGuardTests
{
    [Fact]
    public void The_real_tree_has_no_bad_string_resource_key_and_the_scanner_actually_read_it()
    {
        var csFiles = RepoPaths.AllCSharpFiles;
        var reswFiles = RepoPaths.AllReswFiles;
        csFiles.Length.ShouldBeGreaterThanOrEqualTo(10, "issue #61 built more than 10 .cs files under src/; a guard that finds fewer is not scanning the real tree.");
        reswFiles.Length.ShouldBeGreaterThanOrEqualTo(1, "issue #61 built Strings/en-US/Resources.resw.");

        var reswKeys = StringResourceKeyScanner.LoadReswKeys(reswFiles);
        reswKeys.Count.ShouldBeGreaterThanOrEqualTo(2, "Resources.resw has at least AppDisplayName and ShellTitle.Text.");

        var result = StringResourceKeyScanner.Scan(csFiles, reswKeys);

        // The false pass this guard is most likely to have: zero violations
        // because it scanned nothing (wired-to-nothing's named shape for
        // every scanner test in this project). CallSitesScanned is what
        // distinguishes "every real code lookup resolves" from "found no
        // code lookups at all".
        result.FilesScanned.ShouldBe(csFiles.Length);
        result.CallSitesScanned.ShouldBeGreaterThanOrEqualTo(
            5,
            "the shell status pill, the theme-updated toast, the no-runs-yet toast, and the stop/delete VM confirms all call GetString with a literal key today; a count below that means the scan is not seeing the real call sites.");
        result.Violations.ShouldBeEmpty(
            string.Join("\n", result.Violations.Select(v => $"{v.FilePath}: \"{v.Key}\" {v.Reason}")));
    }

    [Fact]
    public void A_dotted_key_looked_up_from_code_is_flagged_even_when_a_matching_resw_entry_exists()
    {
        // The exact regression this guard exists for: a dotted key is wrong
        // for a code lookup regardless of whether some .resw entry happens
        // to carry that exact dotted name (x:Uid keys do, and are exactly
        // the ones a code lookup must never borrow).
        var reswKeys = new HashSet<string> { "StatusPillSignedIn.Text" };

        WithTempCs(
            """
            namespace Fake;

            public class Caller
            {
                public string Build(DnaEntropyGraph.Presentation.Services.IStringResourceProvider strings)
                {
                    return strings.GetString("StatusPillSignedIn.Text");
                }
            }
            """,
            reswKeys,
            result =>
            {
                result.Violations.ShouldHaveSingleItem();
                result.Violations[0].Key.ShouldBe("StatusPillSignedIn.Text");
                result.Violations[0].Reason.ShouldContain("dotted key");
            });
    }

    [Fact]
    public void A_plain_key_with_no_matching_resw_entry_is_flagged()
    {
        WithTempCs(
            """
            namespace Fake;

            public class Caller
            {
                public string Build(DnaEntropyGraph.Presentation.Services.IStringResourceProvider strings)
                {
                    return strings.GetString("SomeKeyNobodyDefined");
                }
            }
            """,
            new HashSet<string>(),
            result =>
            {
                result.Violations.ShouldHaveSingleItem();
                result.Violations[0].Key.ShouldBe("SomeKeyNobodyDefined");
                result.Violations[0].Reason.ShouldContain("Resources.resw");
            });
    }

    [Fact]
    public void A_plain_key_with_a_matching_resw_entry_is_not_flagged()
    {
        WithTempCs(
            """
            namespace Fake;

            public class Caller
            {
                public string Build(DnaEntropyGraph.Presentation.Services.IStringResourceProvider strings)
                {
                    return strings.GetString("StatusPillSignedIn");
                }
            }
            """,
            new HashSet<string> { "StatusPillSignedIn" },
            result =>
            {
                result.Violations.ShouldBeEmpty();
                result.CallSitesScanned.ShouldBe(1);
            });
    }

    [Fact]
    public void A_settings_store_GetString_call_is_not_mistaken_for_a_resource_lookup()
    {
        // ISettingsStore.GetString("Theme") is a completely different key
        // namespace - a receiver literally named "settingsStore" (or
        // "_settingsStore") must never match, only the "strings"/"_strings"
        // receiver every real IStringResourceProvider field and parameter
        // uses.
        WithTempCs(
            """
            namespace Fake;

            public class Caller
            {
                public string? Build(DnaEntropyGraph.Core.Abstractions.ISettingsStore settingsStore)
                {
                    return settingsStore.GetString("Theme.NotAResourceKey");
                }
            }
            """,
            new HashSet<string>(),
            result =>
            {
                result.Violations.ShouldBeEmpty();
                result.CallSitesScanned.ShouldBe(0);
            });
    }

    [Fact]
    public void A_call_with_a_variable_argument_is_not_flagged()
    {
        WithTempCs(
            """
            namespace Fake;

            public class Caller
            {
                public string Build(DnaEntropyGraph.Presentation.Services.IStringResourceProvider strings, string key)
                {
                    return strings.GetString(key);
                }
            }
            """,
            new HashSet<string>(),
            result =>
            {
                result.Violations.ShouldBeEmpty();
                result.CallSitesScanned.ShouldBe(0);
            });
    }

    [Fact]
    public void A_glob_that_finds_nothing_reports_zero_files_scanned_rather_than_a_silent_pass()
    {
        var result = StringResourceKeyScanner.Scan([], new HashSet<string>());

        result.Violations.ShouldBeEmpty();
        result.FilesScanned.ShouldBe(0);
        result.CallSitesScanned.ShouldBe(0);
    }

    [Fact]
    public void LoadReswKeys_reports_the_real_resw_entry_names()
    {
        var reswFiles = RepoPaths.AllReswFiles;

        var keys = StringResourceKeyScanner.LoadReswKeys(reswFiles);

        keys.ShouldContain("AppDisplayName");
        keys.ShouldContain("ShellTitle.Text");
        keys.ShouldContain("StatusPillSignedIn");
        keys.ShouldContain("StatusPillNotSignedIn");
    }

    private static void WithTempCs(string source, IReadOnlySet<string> reswKeys, Action<StringResourceKeyScanResult> assert)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"deg-guard-{Guid.NewGuid():n}.cs");
        try
        {
            File.WriteAllText(tempFile, source);
            assert(StringResourceKeyScanner.Scan([tempFile], reswKeys));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}
