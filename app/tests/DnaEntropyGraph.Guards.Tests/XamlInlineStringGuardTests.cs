using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// Hard Rule 13's other half (issue #71): every string a user can see lives in
/// <c>Strings/en-US/Resources.resw</c> and is referenced by <c>x:Uid</c>, never
/// written inline in XAML. <see cref="ReswGuardTests"/> checks what is in the
/// .resw; this checks what escaped it.
///
/// An inline string is not a compile error and not a test failure anywhere
/// else. It looks completely right, ships, and is then simply untranslatable
/// and unreviewable by the copy catalog -- which is why it needs a guard
/// rather than a convention.
/// </summary>
public class XamlInlineStringGuardTests
{
    [Fact]
    public void The_real_xaml_has_no_inline_user_facing_string_and_the_scanner_actually_read_it()
    {
        var xamlFiles = RepoPaths.AllXamlFiles;
        xamlFiles.Length.ShouldBeGreaterThanOrEqualTo(2, "issue #61 built App.xaml and MainWindow.xaml.");

        var result = XamlInlineStringScanner.Scan(xamlFiles);

        // The false pass this guard is most likely to have: zero violations
        // because it scanned nothing. FilesScanned is what distinguishes
        // "clean" from "never looked".
        result.FilesScanned.ShouldBe(xamlFiles.Length);
        result.Violations.ShouldBeEmpty(
            "every user-facing string belongs in Resources.resw, referenced with x:Uid (Hard Rule 13).");
    }

    [Fact]
    public void An_inline_Text_value_is_flagged()
    {
        WithTempXaml(
            """<TextBlock Text="Choose a file to analyze" />""",
            result =>
            {
                result.Violations.ShouldHaveSingleItem();
                result.Violations[0].Attribute.ShouldBe("Text");
                result.Violations[0].Value.ShouldBe("Choose a file to analyze");
            });
    }

    [Fact]
    public void A_binding_is_not_flagged()
    {
        WithTempXaml(
            """<TextBlock Text="{x:Bind ViewModel.Title, Mode=OneWay}" />""",
            result => result.Violations.ShouldBeEmpty());
    }

    [Fact]
    public void An_attribute_whose_name_merely_ends_in_a_watched_word_is_not_flagged()
    {
        // MEASURED 2026-09-19 (issue #62): NavigationView.AlwaysShowHeader="True"
        // was flagged as an inline "Header" string before the scanner's
        // regex required a word boundary before the attribute name - a
        // real false positive on legitimate markup, not a violation.
        WithTempXaml(
            """<NavigationView AlwaysShowHeader="True" />""",
            result => result.Violations.ShouldBeEmpty());
    }

    [Fact]
    public void A_value_with_no_letters_is_not_flagged()
    {
        // A number or a coordinate is not user-facing copy, and flagging it
        // would train people to ignore this guard.
        WithTempXaml(
            """<TextBlock Text="42" />""",
            result => result.Violations.ShouldBeEmpty());
    }

    [Fact]
    public void An_attribute_that_is_scanned_and_clean_still_counts_as_scanned()
    {
        // Separates "no violations because every value was a binding" from
        // "no violations because the regex matched nothing at all" -- the two
        // are indistinguishable by the violation list alone.
        WithTempXaml(
            """<TextBlock Text="{x:Bind Title}" Header="{x:Bind Sub}" />""",
            result =>
            {
                result.Violations.ShouldBeEmpty();
                result.AttributesScanned.ShouldBe(2);
            });
    }

    [Fact]
    public void A_glob_that_finds_nothing_reports_zero_files_scanned_rather_than_a_silent_pass()
    {
        var result = XamlInlineStringScanner.Scan([]);

        result.Violations.ShouldBeEmpty();
        result.FilesScanned.ShouldBe(0);
        result.AttributesScanned.ShouldBe(0);
    }

    private static void WithTempXaml(string markup, Action<XamlInlineStringScanResult> assert)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"deg-guard-{Guid.NewGuid():n}.xaml");
        try
        {
            File.WriteAllText(tempFile, markup);
            assert(XamlInlineStringScanner.Scan([tempFile]));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}
