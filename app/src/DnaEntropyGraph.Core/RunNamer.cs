using System.Globalization;
using System.Text.RegularExpressions;

namespace DnaEntropyGraph.Core;

/// <summary>
/// Turns <see cref="RunOptions.NameTemplate"/> into a run or batch name that is safe as a Windows folder name
/// (issue #63). Tokens: <c>{file}</c> (input file name without folder or extension), <c>{date}</c> (default
/// <c>yyyy-MM-dd</c>), <c>{time}</c> (default <c>HHmm</c>), <c>{model}</c> and <c>{n}</c>; <c>{date:...}</c> and
/// <c>{time:...}</c> take a .NET custom format. An unknown token stays as typed so the user can see the typo.
/// A name already taken gets <c>_2</c>, <c>_3</c> and so on. Pure: it never touches the disk, so the caller passes
/// the names already in use.
/// </summary>
public static partial class RunNamer
{
    /// <summary>The longest name produced, suffix included (leaves room under Windows' 260 character path limit).</summary>
    public const int MaxLength = 100;

    private const string Fallback = "run";
    private const string PastedName = "pasted";

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <param name="template">The template; null or blank gives the name <c>run</c>.</param>
    /// <param name="inputFile">The input's path or name, or null for a pasted sequence.</param>
    /// <param name="modelId">Wire model id, e.g. <c>evo2_7b</c>.</param>
    /// <param name="now">Local time stamped into <c>{date}</c> and <c>{time}</c>.</param>
    /// <param name="n">The 1-based position in the batch for <c>{n}</c>.</param>
    /// <param name="existingNames">Names already used (earlier runs, earlier items of this batch). Compared ignoring case.</param>
    public static string Resolve(
        string? template,
        string? inputFile,
        string modelId,
        DateTime now,
        int n,
        IEnumerable<string> existingNames)
    {
        ArgumentNullException.ThrowIfNull(existingNames);
        var stem = string.IsNullOrWhiteSpace(inputFile) ? PastedName : Path.GetFileNameWithoutExtension(inputFile.Trim());
        var expanded = TokenPattern().Replace(template ?? string.Empty, match => Expand(match, stem, modelId, now, n));
        var baseName = Truncate(Sanitize(expanded), MaxLength);

        var taken = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(baseName))
        {
            return baseName;
        }

        for (var suffixNumber = 2; ; suffixNumber++)
        {
            var suffix = "_" + suffixNumber.ToString(CultureInfo.InvariantCulture);
            var candidate = Truncate(baseName, MaxLength - suffix.Length) + suffix;
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    private static string Expand(Match match, string stem, string modelId, DateTime now, int n)
    {
        var format = match.Groups["format"].Success ? match.Groups["format"].Value : null;
        return match.Groups["token"].Value.ToLowerInvariant() switch
        {
            "file" => stem,
            "model" => modelId,
            "n" => n.ToString(CultureInfo.InvariantCulture),
            "date" => FormatTime(now, format, "yyyy-MM-dd"),
            "time" => FormatTime(now, format, "HHmm"),
            _ => match.Value,
        };
    }

    private static string FormatTime(DateTime now, string? format, string fallbackFormat)
    {
        try
        {
            return now.ToString(string.IsNullOrEmpty(format) ? fallbackFormat : format, CultureInfo.InvariantCulture);
        }
        catch (FormatException)
        {
            return now.ToString(fallbackFormat, CultureInfo.InvariantCulture);
        }
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray());
        cleaned = TrimEnd(cleaned.Trim());
        if (cleaned.Length == 0)
        {
            return Fallback;
        }

        // CON, NUL, COM1 ... are device names to Windows even with an extension ("lpt9.txt").
        var dot = cleaned.IndexOf('.', StringComparison.Ordinal);
        var deviceStem = dot < 0 ? cleaned : cleaned[..dot];
        return ReservedDeviceNames.Contains(deviceStem.TrimEnd()) ? Fallback + "_" + cleaned : cleaned;
    }

    private static string Truncate(string name, int max) => name.Length <= max ? name : TrimEnd(name[..max]);

    // Windows drops trailing dots and spaces from a folder name, so a name must not end in either.
    private static string TrimEnd(string name) => name.TrimEnd('.', ' ');

    [GeneratedRegex(@"\{(?<token>[A-Za-z]+)(?::(?<format>[^}]*))?\}")]
    private static partial Regex TokenPattern();
}
