using System.Text.RegularExpressions;

namespace DnaEntropyGraph.Guards.Tests;

public sealed record CsprojViolation(string ProjectPath, string PackageId, string Reason);

/// <summary>
/// Hard Rule 6 ("nothing else imports torch, evo2 or flash_attn"; no
/// inference package in any .csproj) and Hard Rule 7 (only
/// DnaEntropyGraph.Cloud may reference Google.*).
/// </summary>
internal static class CsprojDependencyScanner
{
    private static readonly Regex PackageReferenceRegex = new(
        "<PackageReference\\s+Include=\"([^\"]+)\"",
        RegexOptions.Compiled);

    private static readonly string[] InferencePackageNeedles =
    [
        "torch", "onnx", "evo2", "flash-attn", "flash_attn", "tensorflow", "TorchSharp",
    ];

    public static IReadOnlyList<CsprojViolation> Scan(IEnumerable<string> csprojPaths)
    {
        var violations = new List<CsprojViolation>();

        foreach (var path in csprojPaths)
        {
            var text = File.ReadAllText(path);
            var isCloudProject = path.Replace('\\', '/').Contains("/DnaEntropyGraph.Cloud/", StringComparison.OrdinalIgnoreCase);

            foreach (Match match in PackageReferenceRegex.Matches(text))
            {
                var packageId = match.Groups[1].Value;

                if (!isCloudProject && packageId.StartsWith("Google.", StringComparison.Ordinal))
                {
                    violations.Add(new CsprojViolation(path, packageId, "Google.* outside DnaEntropyGraph.Cloud (Hard Rule 7)."));
                }

                foreach (var needle in InferencePackageNeedles)
                {
                    if (packageId.Contains(needle, StringComparison.OrdinalIgnoreCase))
                    {
                        violations.Add(new CsprojViolation(path, packageId, $"looks like an inference package ('{needle}', Hard Rule 6)."));
                        break;
                    }
                }
            }
        }

        return violations;
    }
}
