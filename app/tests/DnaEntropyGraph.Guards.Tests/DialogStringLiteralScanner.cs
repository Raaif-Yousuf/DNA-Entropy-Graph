using System.Text;
using System.Text.RegularExpressions;

namespace DnaEntropyGraph.Guards.Tests;

public sealed record DialogStringLiteralViolation(string FilePath, string MethodName, int ArgumentIndex, string Value, int LineNumber);

/// <summary>
/// What a scan actually looked at, not just what it objected to - the same
/// shape <see cref="ReswScanResult"/> and <see cref="XamlInlineStringScanResult"/>
/// use, for the same reason (wired-to-nothing: a violation count of zero
/// means "found nothing to check" and "checked everything and it was fine"
/// equally well unless something else tells the two apart).
/// </summary>
public sealed record DialogStringLiteralScanResult(int FilesScanned, int CallSitesScanned, IReadOnlyList<DialogStringLiteralViolation> Violations);

/// <summary>
/// Hard Rule 13's C# half beyond XAML (issue #71): a call site that hands a
/// literal title or message straight to a dialog-shaped API
/// (<c>IDialogService.ConfirmAsync</c>, <c>IToastService.ShowToast</c>)
/// bypasses Resources.resw with no compile error and no other guard
/// noticing. Regex-based on purpose, matching <see cref="XamlInlineStringScanner"/>'s
/// own approach rather than a full Roslyn parse: comments are stripped
/// first (mirroring <see cref="CodeBehindScanner"/>) so a doc comment
/// mentioning "ConfirmAsync(" is not a false call site, and a leading '.'
/// is required so an interface/method *declaration* (no dot before the
/// name) is never mistaken for a call.
/// </summary>
internal static class DialogStringLiteralScanner
{
    private static readonly string[] DialogMethodNames = ["ConfirmAsync", "ShowToast"];
    private static readonly Regex LineCommentRegex = new("//.*$", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex BlockCommentRegex = new(@"/\*.*?\*/", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex StringLiteralArgumentRegex = new("^\\$?\"(.*)\"$", RegexOptions.Singleline | RegexOptions.Compiled);

    public static DialogStringLiteralScanResult Scan(IEnumerable<string> csharpPaths)
    {
        var violations = new List<DialogStringLiteralViolation>();
        var filesScanned = 0;
        var callSitesScanned = 0;

        foreach (var path in csharpPaths)
        {
            filesScanned++;
            var stripped = Strip(File.ReadAllText(path));

            foreach (var methodName in DialogMethodNames)
            {
                foreach (Match call in Regex.Matches(stripped, $@"\.{methodName}\s*\(([^;]*?)\)\s*[;,).]", RegexOptions.Singleline))
                {
                    callSitesScanned++;
                    var lineNumber = stripped[..call.Index].Count(c => c == '\n') + 1;
                    var arguments = SplitTopLevelArguments(call.Groups[1].Value);

                    for (var i = 0; i < arguments.Count; i++)
                    {
                        var literalMatch = StringLiteralArgumentRegex.Match(arguments[i].Trim());
                        if (!literalMatch.Success)
                        {
                            continue;
                        }

                        var value = literalMatch.Groups[1].Value;
                        if (value.Length == 0 || !value.Any(char.IsLetter))
                        {
                            // Empty, or no letters (an id, a format placeholder) - not user-facing copy.
                            continue;
                        }

                        violations.Add(new DialogStringLiteralViolation(path, methodName, i, value, lineNumber));
                    }
                }
            }
        }

        return new DialogStringLiteralScanResult(filesScanned, callSitesScanned, violations);
    }

    /// <summary>
    /// A top-level comma split that does not break on a comma inside a
    /// string literal or a nested parenthesized expression. Good enough for
    /// every real call site this scanner's own inventory of
    /// App/Services and Presentation/ViewModels contains today (title,
    /// message[, cancellationToken]); a future call nesting a comma-bearing
    /// expression as an argument would need a real parser, not a bigger regex.
    /// </summary>
    private static List<string> SplitTopLevelArguments(string argumentsText)
    {
        var results = new List<string>();
        var depth = 0;
        var inString = false;
        var current = new StringBuilder();

        foreach (var ch in argumentsText)
        {
            if (ch == '"')
            {
                inString = !inString;
            }

            if (!inString && ch == '(')
            {
                depth++;
            }

            if (!inString && ch == ')')
            {
                depth--;
            }

            if (ch == ',' && depth == 0 && !inString)
            {
                results.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(ch);
        }

        if (current.Length > 0)
        {
            results.Add(current.ToString());
        }

        return results;
    }

    private static string Strip(string source)
    {
        var noBlockComments = BlockCommentRegex.Replace(source, string.Empty);
        return LineCommentRegex.Replace(noBlockComments, string.Empty);
    }
}
