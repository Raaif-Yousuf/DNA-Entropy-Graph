using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// Issue #71's remaining "Done when" line: "C# string literals passed to
/// dialogs". <see cref="ReswGuardTests"/> only reads .resw and
/// <see cref="XamlInlineStringGuardTests"/> only reads .xaml - neither one
/// looks inside a .cs method body, so a call site that hands a literal
/// straight to <c>IDialogService.ConfirmAsync</c> or
/// <c>IToastService.ShowToast</c> was invisible to every existing guard.
/// This is that missing scan.
/// </summary>
public class DialogStringLiteralGuardTests
{
    [Fact]
    public void The_real_tree_has_no_literal_dialog_argument_and_the_scanner_actually_read_it()
    {
        var csFiles = RepoPaths.AllCSharpFiles;
        csFiles.Length.ShouldBeGreaterThanOrEqualTo(10, "issue #61 built more than 10 .cs files under src/; a guard that finds fewer is not scanning the real tree.");

        var result = DialogStringLiteralScanner.Scan(csFiles);

        result.FilesScanned.ShouldBe(csFiles.Length);
        result.Violations.ShouldBeEmpty(
            string.Join("\n", result.Violations.Select(v => $"{v.FilePath}:{v.LineNumber} - {v.MethodName} arg {v.ArgumentIndex}: \"{v.Value}\"")));
    }

    [Fact]
    public void A_literal_title_and_message_passed_to_ConfirmAsync_are_both_flagged()
    {
        WithTempCs(
            """
            namespace Fake;

            public class Caller
            {
                public async System.Threading.Tasks.Task DoIt(DnaEntropyGraph.Core.Abstractions.IDialogService dialogService, System.Threading.CancellationToken token)
                {
                    await dialogService.ConfirmAsync("Delete this VM?", "This cannot be undone.", token);
                }
            }
            """,
            result =>
            {
                result.Violations.Count.ShouldBe(2);
                result.Violations[0].MethodName.ShouldBe("ConfirmAsync");
                result.Violations[0].ArgumentIndex.ShouldBe(0);
                result.Violations[0].Value.ShouldBe("Delete this VM?");
                result.Violations[1].ArgumentIndex.ShouldBe(1);
                result.Violations[1].Value.ShouldBe("This cannot be undone.");
            });
    }

    [Fact]
    public void A_literal_argument_to_ShowToast_is_flagged()
    {
        WithTempCs(
            """
            namespace Fake;

            public class Caller
            {
                public void DoIt(DnaEntropyGraph.Core.Abstractions.IToastService toastService)
                {
                    toastService.ShowToast("Theme updated", "Dark");
                }
            }
            """,
            result => result.Violations.Count.ShouldBe(2));
    }

    [Fact]
    public void A_call_with_only_variable_arguments_is_not_flagged()
    {
        WithTempCs(
            """
            namespace Fake;

            public class Caller
            {
                public void DoIt(DnaEntropyGraph.Core.Abstractions.IToastService toastService, string title, string body)
                {
                    toastService.ShowToast(title, body);
                }
            }
            """,
            result => result.Violations.ShouldBeEmpty());
    }

    [Fact]
    public void A_call_site_that_is_scanned_and_clean_still_counts_as_scanned()
    {
        // Separates "no violations because every argument was a variable"
        // from "no violations because the regex matched nothing at all" -
        // the same distinction ReswScanner and XamlInlineStringScanner
        // already make (wired-to-nothing's named false pass for this guard shape).
        WithTempCs(
            """
            namespace Fake;

            public class Caller
            {
                public void DoIt(DnaEntropyGraph.Core.Abstractions.IToastService toastService, string title, string body)
                {
                    toastService.ShowToast(title, body);
                }
            }
            """,
            result =>
            {
                result.Violations.ShouldBeEmpty();
                result.CallSitesScanned.ShouldBe(1);
            });
    }

    [Fact]
    public void A_method_declaration_with_string_parameters_is_not_a_call_site()
    {
        // ConfirmAsync's own interface declaration has string-typed
        // parameters but no literal argument and no leading dot - a scanner
        // that cannot tell a declaration from a call site would flag every
        // interface and implementation in the codebase.
        WithTempCs(
            """
            namespace Fake;

            public interface IDialogService
            {
                System.Threading.Tasks.Task<bool> ConfirmAsync(string title, string message, System.Threading.CancellationToken cancellationToken);
            }
            """,
            result =>
            {
                result.Violations.ShouldBeEmpty();
                result.CallSitesScanned.ShouldBe(0);
            });
    }

    [Fact]
    public void A_glob_that_finds_nothing_reports_zero_files_scanned_rather_than_a_silent_pass()
    {
        var result = DialogStringLiteralScanner.Scan([]);

        result.Violations.ShouldBeEmpty();
        result.FilesScanned.ShouldBe(0);
        result.CallSitesScanned.ShouldBe(0);
    }

    private static void WithTempCs(string source, Action<DialogStringLiteralScanResult> assert)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"deg-guard-{Guid.NewGuid():n}.cs");
        try
        {
            File.WriteAllText(tempFile, source);
            assert(DialogStringLiteralScanner.Scan([tempFile]));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}
