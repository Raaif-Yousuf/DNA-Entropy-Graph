using System.Text.RegularExpressions;

namespace DnaEntropyGraph.Guards.Tests;

public sealed record XamlInlineStringViolation(string FilePath, string Attribute, string Value);

/// <summary>
/// What a scan actually looked at, not just what it objected to. A violation
/// count of zero means "found nothing to check" and "checked everything and it
/// was fine" equally well, and only these counters tell the two apart -- the
/// same shape <see cref="ReswScanner"/> guards against.
/// </summary>
public sealed record XamlInlineStringScanResult(
    int FilesScanned,
    int AttributesScanned,
    IReadOnlyList<XamlInlineStringViolation> Violations);

/// <summary>
/// Hard Rule 13: no inline user-facing string in XAML - it belongs in
/// Strings/en-US/Resources.resw, referenced via x:Uid. Flags a literal,
/// letter-containing value on a handful of well-known user-facing
/// attributes that is not a binding/markup extension.
/// </summary>
internal static class XamlInlineStringScanner
{
    private static readonly string[] UserFacingAttributes = ["Text", "Content", "Header", "PlaceholderText"];

    public static XamlInlineStringScanResult Scan(IEnumerable<string> xamlPaths)
    {
        var violations = new List<XamlInlineStringViolation>();
        var filesScanned = 0;
        var attributesScanned = 0;

        foreach (var path in xamlPaths)
        {
            filesScanned++;
            var text = File.ReadAllText(path);

            foreach (var attribute in UserFacingAttributes)
            {
                // \b before the attribute name: without it, "Header" also
                // matches inside "AlwaysShowHeader=" (MEASURED 2026-09-19,
                // issue #62 - NavigationView.AlwaysShowHeader="True" was
                // flagged as an inline "Header" string, a real false
                // positive on legitimate markup, not a violation).
                foreach (Match match in Regex.Matches(text, $"\\b{attribute}=\"([^\"]*)\""))
                {
                    attributesScanned++;
                    var value = match.Groups[1].Value;

                    if (value.Length == 0 || value.StartsWith('{'))
                    {
                        // Empty, or a markup extension ({x:Bind ...}, {Binding ...}, {StaticResource ...}).
                        continue;
                    }

                    if (!value.Any(char.IsLetter))
                    {
                        // No letters: not user-facing text (a number, an enum value like "Center").
                        continue;
                    }

                    violations.Add(new XamlInlineStringViolation(path, attribute, value));
                }
            }
        }

        return new XamlInlineStringScanResult(filesScanned, attributesScanned, violations);
    }
}
