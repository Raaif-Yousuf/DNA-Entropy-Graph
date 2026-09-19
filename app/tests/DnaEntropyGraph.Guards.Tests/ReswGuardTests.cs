using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

public class ReswGuardTests
{
    [Fact]
    public void The_real_resw_file_has_no_em_dash_and_the_scanner_actually_read_its_entries()
    {
        var reswFiles = RepoPaths.AllReswFiles;
        reswFiles.Length.ShouldBeGreaterThanOrEqualTo(1, "issue #61 built Strings/en-US/Resources.resw.");

        var result = ReswScanner.Scan(reswFiles);

        // The named false-pass: "it passes on an empty .resw and on one it
        // never found." Both of those look identical to a violation count
        // of zero unless the scan also reports what it actually read.
        result.FilesScanned.ShouldBeGreaterThanOrEqualTo(1);
        result.DataEntriesScanned.ShouldBeGreaterThanOrEqualTo(2, "Resources.resw has at least AppDisplayName and ShellTitle.Text.");
        result.Violations.ShouldBeEmpty();
    }

    [Fact]
    public void An_em_dash_in_a_resw_value_is_flagged()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"deg-guard-{Guid.NewGuid():n}.resw");
        try
        {
            File.WriteAllText(tempFile, """
                <root>
                  <data name="Bad" xml:space="preserve">
                    <value>Choose Delete — or Keep alive</value>
                  </data>
                </root>
                """.Replace("\\u2014", "—"));

            var result = ReswScanner.Scan([tempFile]);

            result.DataEntriesScanned.ShouldBe(1);
            result.Violations.ShouldHaveSingleItem();
            result.Violations[0].DataName.ShouldBe("Bad");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void An_empty_resw_reports_zero_entries_scanned_rather_than_a_silent_pass()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"deg-guard-{Guid.NewGuid():n}.resw");
        try
        {
            File.WriteAllText(tempFile, "<root></root>");

            var result = ReswScanner.Scan([tempFile]);

            // This is the exact false-pass named in the brief: zero
            // violations here means "found nothing to check", not "checked
            // everything and it was fine" - DataEntriesScanned is what
            // tells the two apart, and a real guard test must look at it.
            result.Violations.ShouldBeEmpty();
            result.DataEntriesScanned.ShouldBe(0);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void A_glob_that_finds_nothing_reports_zero_files_scanned_rather_than_a_silent_pass()
    {
        var result = ReswScanner.Scan([]);

        result.Violations.ShouldBeEmpty();
        result.FilesScanned.ShouldBe(0);
    }
}
