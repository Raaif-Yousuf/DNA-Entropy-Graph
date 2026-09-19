using System.Xml.Linq;

namespace DnaEntropyGraph.Guards.Tests;

public sealed record ReswViolation(string FilePath, string DataName, string Reason);

public sealed record ReswScanResult(int FilesScanned, int DataEntriesScanned, IReadOnlyList<ReswViolation> Violations);

/// <summary>
/// Hard Rule 13: no em dash anywhere in a .resw. Reports how many files and
/// how many &lt;data&gt; entries it actually read, not just a violation
/// count - an em-dash scan that passes because the .resw was empty, or
/// because the path glob found nothing, is the exact false-pass the
/// wired-to-nothing skill calls out for this guard by name.
/// </summary>
internal static class ReswScanner
{
    private const char EmDash = '—';

    public static ReswScanResult Scan(IEnumerable<string> reswPaths)
    {
        var violations = new List<ReswViolation>();
        var filesScanned = 0;
        var dataEntriesScanned = 0;

        foreach (var path in reswPaths)
        {
            filesScanned++;
            var doc = XDocument.Load(path);

            foreach (var data in doc.Root?.Elements("data") ?? Enumerable.Empty<XElement>())
            {
                dataEntriesScanned++;
                var name = data.Attribute("name")?.Value ?? "(unnamed)";
                var value = data.Element("value")?.Value ?? string.Empty;

                if (value.Contains(EmDash))
                {
                    violations.Add(new ReswViolation(path, name, "contains an em dash (U+2014), forbidden in .resw by Hard Rule 13."));
                }
            }
        }

        return new ReswScanResult(filesScanned, dataEntriesScanned, violations);
    }
}
