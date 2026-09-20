using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DnaEntropyGraph.Guards.Tests;

public sealed record StringResourceKeyViolation(string FilePath, string Key, string Reason);

/// <summary>
/// What a scan actually looked at, not just what it objected to - the same
/// shape <see cref="ReswScanResult"/> and <see cref="DialogStringLiteralScanResult"/>
/// use, for the same reason (wired-to-nothing: a violation count of zero
/// means "found nothing to check" and "checked everything and it was fine"
/// equally well unless something else tells the two apart).
/// </summary>
public sealed record StringResourceKeyScanResult(int FilesScanned, int CallSitesScanned, IReadOnlyList<StringResourceKeyViolation> Violations);

/// <summary>
/// Hard Rule 13's remaining missed shape (issue #424's fix): a C# call site
/// that hands a literal key straight to <c>IStringResourceProvider.GetString</c>
/// can compile, resolve nothing at runtime, and fall back to showing the key
/// itself on screen - MEASURED: the shell's title bar showed the literal
/// text "StatusPillSignedIn.Text" instead of "Signed in - ...". Neither
/// <see cref="ReswGuardTests"/> (reads only .resw) nor
/// <see cref="DialogStringLiteralGuardTests"/> (flags a literal that should
/// have been a resource key at all) catches this - this is the guard for
/// "the key is a resource key, but the wrong *shape* of one".
///
/// A <c>.resw</c> entry named <c>Foo.Text</c> compiles into the PRI as the
/// nested resource path <c>Foo/Text</c> - the convention <c>x:Uid</c> relies
/// on for a XAML binding (<c>x:Uid="Foo"</c> resolves <c>Foo.Text</c>/<c>Foo.Content</c>
/// automatically). A code call through <c>ResourceLoader.GetString(key)</c>
/// has no such convention: it looks up the literal key string, so a dotted
/// key misses the compiled nested path. Every key reached from code must
/// therefore be the plain (non-dotted) form; a key also reached via
/// <c>x:Uid</c> keeps the dotted form, and such keys are never passed to
/// <c>GetString</c> from code in the first place (see
/// <c>ShellViewModel.BuildStatusPillText</c>'s own comment for the full
/// reasoning this guard enforces mechanically).
///
/// Regex-based on purpose, matching this project's other scanners: comments
/// are stripped first (mirroring <see cref="DialogStringLiteralScanner"/>)
/// and only the <c>strings</c>/<c>_strings</c> receiver name every
/// <c>IStringResourceProvider</c> field and parameter uses throughout this
/// codebase is matched, so <c>ISettingsStore.GetString</c> (a completely
/// different key namespace - "Theme", "OutputFolder", "MainWindowPlacement")
/// is never mistaken for a resource lookup.
/// </summary>
internal static class StringResourceKeyScanner
{
    private static readonly Regex LineCommentRegex = new("//.*$", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex BlockCommentRegex = new(@"/\*.*?\*/", RegexOptions.Singleline | RegexOptions.Compiled);

    // (?<![A-Za-z0-9_.]) rules out matching the tail of some other
    // identifier (e.g. a hypothetical "otherStrings.GetString(" or a
    // "x.strings.GetString(" member-access chain) - only a bare "strings"
    // or "_strings" receiver counts, the exact convention every real call
    // site in this codebase already follows.
    private static readonly Regex CallRegex = new(
        "(?<![A-Za-z0-9_.])(?:_strings|strings)\\.GetString\\(\\s*\"((?:[^\"\\\\]|\\\\.)*)\"\\s*\\)",
        RegexOptions.Compiled);

    public static StringResourceKeyScanResult Scan(IEnumerable<string> csharpPaths, IReadOnlySet<string> reswKeys)
    {
        var violations = new List<StringResourceKeyViolation>();
        var filesScanned = 0;
        var callSitesScanned = 0;

        foreach (var path in csharpPaths)
        {
            filesScanned++;
            var stripped = Strip(File.ReadAllText(path));

            foreach (Match match in CallRegex.Matches(stripped))
            {
                callSitesScanned++;
                var key = match.Groups[1].Value;

                if (key.Contains('.'))
                {
                    violations.Add(new StringResourceKeyViolation(
                        path,
                        key,
                        "is a dotted key looked up from code - ResourceLoader.GetString resolves only the plain, non-dotted resw entry name (Hard Rule 13)."));
                    continue;
                }

                if (!reswKeys.Contains(key))
                {
                    violations.Add(new StringResourceKeyViolation(path, key, "has no matching <data name=\"...\"> entry in Resources.resw."));
                }
            }
        }

        return new StringResourceKeyScanResult(filesScanned, callSitesScanned, violations);
    }

    /// <summary>Every <c>&lt;data name="..."&gt;</c> entry name across the given .resw files, exactly as compiled - the membership set <see cref="Scan"/> checks a plain-form code key against.</summary>
    public static IReadOnlySet<string> LoadReswKeys(IEnumerable<string> reswPaths)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var path in reswPaths)
        {
            var doc = XDocument.Load(path);
            foreach (var data in doc.Root?.Elements("data") ?? Enumerable.Empty<XElement>())
            {
                var name = data.Attribute("name")?.Value;
                if (!string.IsNullOrEmpty(name))
                {
                    keys.Add(name);
                }
            }
        }

        return keys;
    }

    private static string Strip(string source)
    {
        var noBlockComments = BlockCommentRegex.Replace(source, string.Empty);
        return LineCommentRegex.Replace(noBlockComments, string.Empty);
    }
}
