using System.Globalization;

namespace DnaEntropyGraph.Persistence;

/// <summary>
/// Shared TEXT/INTEGER &lt;-&gt; .NET conversions for every repository in this
/// project. SQLite has no native <see cref="DateTimeOffset"/> or
/// <see cref="bool"/> column type, so every repository stores round-trip
/// ISO-8601 strings and 0/1 integers explicitly rather than trusting
/// Dapper/Microsoft.Data.Sqlite's implicit type mapping for either.
/// </summary>
internal static class Iso
{
    public static string? Format(DateTimeOffset? value) => value?.ToString("o", CultureInfo.InvariantCulture);

    public static DateTimeOffset? Parse(string? value) =>
        value is null ? null : DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    public static bool? ToBool(long? value) => value is null ? null : value != 0;

    public static long? ToLong(bool? value) => value is null ? null : (value.Value ? 1 : 0);
}
