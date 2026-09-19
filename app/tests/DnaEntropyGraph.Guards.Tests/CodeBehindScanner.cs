using System.Text.RegularExpressions;

namespace DnaEntropyGraph.Guards.Tests;

public sealed record CodeBehindViolation(string FilePath, string Keyword, int LineNumber);

/// <summary>
/// Hard Rule 8: a <c>*.xaml.cs</c> file holds <c>InitializeComponent()</c>,
/// constructor DI, and nothing else that branches. Strips comments and
/// string literals first so a doc comment or a log message that happens to
/// contain the English word "if" is not a false positive - a text-only
/// scan without that step would be a guard that cannot help but distrust
/// its own coverage.
/// </summary>
internal static class CodeBehindScanner
{
    private static readonly string[] BranchingKeywords = ["if", "for", "foreach", "while", "switch", "catch"];
    private static readonly Regex LineCommentRegex = new("//.*$", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex BlockCommentRegex = new(@"/\*.*?\*/", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex StringLiteralRegex = new("\"(?:\\\\.|[^\"\\\\])*\"", RegexOptions.Compiled);

    public static IReadOnlyList<CodeBehindViolation> Scan(IEnumerable<string> filePaths)
    {
        var violations = new List<CodeBehindViolation>();

        foreach (var path in filePaths)
        {
            var stripped = Strip(File.ReadAllText(path));
            var lines = stripped.Split('\n');

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];

                foreach (var keyword in BranchingKeywords)
                {
                    if (Regex.IsMatch(line, $@"\b{keyword}\s*\("))
                    {
                        violations.Add(new CodeBehindViolation(path, keyword, i + 1));
                    }
                }

                if (line.Contains("&&", StringComparison.Ordinal) || line.Contains("||", StringComparison.Ordinal))
                {
                    violations.Add(new CodeBehindViolation(path, "&&/||", i + 1));
                }

                // Ternary `?:` - a bare '?' not part of '?.' (null-conditional) or '??'
                // (null-coalescing), followed later by a ':' on the same line.
                if (Regex.IsMatch(line, @"[^?]\?(?![.?])") && line.Contains(':'))
                {
                    violations.Add(new CodeBehindViolation(path, "?:", i + 1));
                }
            }
        }

        return violations;
    }

    private static string Strip(string source)
    {
        var noBlockComments = BlockCommentRegex.Replace(source, string.Empty);
        var noLineComments = LineCommentRegex.Replace(noBlockComments, string.Empty);
        return StringLiteralRegex.Replace(noLineComments, "\"\"");
    }
}
