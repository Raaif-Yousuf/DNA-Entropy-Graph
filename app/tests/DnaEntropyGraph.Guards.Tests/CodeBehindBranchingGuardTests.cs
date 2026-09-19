using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

public class CodeBehindBranchingGuardTests
{
    [Fact]
    public void The_real_xaml_cs_files_have_no_branching()
    {
        var files = RepoPaths.AllXamlCsFiles;

        // Vacuity check: App.xaml.cs and MainWindow.xaml.cs must both be found.
        files.Length.ShouldBeGreaterThanOrEqualTo(2, "issue #61 built App.xaml.cs and MainWindow.xaml.cs; a guard that finds fewer is not scanning the real tree.");

        var violations = CodeBehindScanner.Scan(files);

        violations.ShouldBeEmpty(string.Join("\n", violations.Select(v => $"{v.FilePath}:{v.LineNumber} - {v.Keyword}")));
    }

    [Fact]
    public void An_if_statement_in_a_xaml_cs_file_is_flagged()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"deg-guard-{Guid.NewGuid():n}.xaml.cs");
        try
        {
            File.WriteAllText(tempFile, """
                namespace Fake;

                public sealed partial class BadPage
                {
                    public BadPage()
                    {
                        InitializeComponent();
                        if (SomeCondition())
                        {
                            DoSomething();
                        }
                    }
                }
                """);

            var violations = CodeBehindScanner.Scan([tempFile]);

            violations.ShouldContain(v => v.Keyword == "if");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void A_word_that_merely_contains_if_in_a_comment_is_not_flagged()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"deg-guard-{Guid.NewGuid():n}.xaml.cs");
        try
        {
            File.WriteAllText(tempFile, """
                namespace Fake;

                // This constructor exists if a designer ever needs it - see docs.
                public sealed partial class OkPage
                {
                    public OkPage()
                    {
                        InitializeComponent();
                    }
                }
                """);

            var violations = CodeBehindScanner.Scan([tempFile]);

            violations.ShouldBeEmpty();
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void A_ternary_expression_is_flagged()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"deg-guard-{Guid.NewGuid():n}.xaml.cs");
        try
        {
            File.WriteAllText(tempFile, """
                namespace Fake;

                public sealed partial class BadPage
                {
                    private readonly string _label;

                    public BadPage(bool flag)
                    {
                        InitializeComponent();
                        _label = flag ? "on" : "off";
                    }
                }
                """);

            var violations = CodeBehindScanner.Scan([tempFile]);

            violations.ShouldContain(v => v.Keyword == "?:");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}
