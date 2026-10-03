using System.Globalization;
using System.Text.Json;

namespace DnaEntropyGraph.Core.Cost;

/// <summary>Why a price list could not be used. Every one is answered the same way for the user (reinstall the app), but tests and logs name the cause.</summary>
public enum PricingProblem
{
    FileMissing,
    Unreadable,
    Malformed,
    UnsupportedSchema,
    InvalidValue,
}

/// <summary>A parsed price list, or the one problem that stopped it. <see cref="Detail"/> is for logs and tests, never shown to a user.</summary>
public sealed record PricingLoadResult(PricingTable? Table, PricingProblem? Problem, string? Detail)
{
    public bool IsOk => Table is not null;

    public static PricingLoadResult Fail(PricingProblem problem, string detail) => new(null, problem, detail);
}

/// <summary>Hourly prices of one machine type with its GPU. <see cref="SpotUsdPerHour"/> is null when the price is not known, which is not the same as free.</summary>
public sealed record MachinePrice(string MachineType, string Accelerator, double OnDemandUsdPerHour, double? SpotUsdPerHour);

/// <summary>
/// The price list that ships with each release (<c>Assets/pricing.json</c>, issue #98, docs/cloud_design.md section 10).
/// Every figure is an estimate with an <see cref="AsOf"/> date and a <see cref="Source"/> note: the app has no access to the
/// user's real bill. Parsing is pure (a string in, a result out) and never throws for bad input.
/// </summary>
public sealed class PricingTable
{
    /// <summary>The only file format this build reads. A file with another number came from another build of the app.</summary>
    public const int SupportedSchema = 1;

    /// <summary>Google bills a month as 730 hours, which is how a per-GB-month disk price becomes an hourly one.</summary>
    public const double HoursPerMonth = 730;

    private readonly Dictionary<string, MachinePrice> _machines;

    private PricingTable(DateOnly asOf, string region, string source, double diskSizeGb, double diskUsdPerGbMonth, Dictionary<string, MachinePrice> machines)
    {
        AsOf = asOf;
        Region = region;
        Source = source;
        DiskSizeGb = diskSizeGb;
        DiskUsdPerGbMonth = diskUsdPerGbMonth;
        _machines = machines;
    }

    public DateOnly AsOf { get; }

    public string Region { get; }

    public string Source { get; }

    public double DiskSizeGb { get; }

    public double DiskUsdPerGbMonth { get; }

    /// <summary>What the boot disk costs for each hour the VM exists, whether it runs or not.</summary>
    public double DiskUsdPerHour => DiskSizeGb * DiskUsdPerGbMonth / HoursPerMonth;

    public MachinePrice? Find(string machineType) => _machines.GetValueOrDefault(machineType);

    public static PricingLoadResult Parse(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json ?? string.Empty);
        }
        catch (JsonException ex)
        {
            return PricingLoadResult.Fail(PricingProblem.Malformed, "Not valid JSON: " + ex.Message);
        }

        using (document)
        {
            return Read(document.RootElement);
        }
    }

    private static PricingLoadResult Read(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return PricingLoadResult.Fail(PricingProblem.Malformed, "The top level is not an object.");
        }

        if (!root.TryGetProperty("schema", out var schema) || !schema.TryGetInt32(out var version) || version != SupportedSchema)
        {
            return PricingLoadResult.Fail(PricingProblem.UnsupportedSchema, $"Expected \"schema\": {SupportedSchema}.");
        }

        if (!TryText(root, "asOf", out var asOfText)
            || !DateOnly.TryParseExact(asOfText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var asOf))
        {
            return Invalid("\"asOf\" must be a date written yyyy-MM-dd.");
        }

        if (!TryText(root, "currency", out var currency) || currency != "USD")
        {
            return Invalid("\"currency\" must be \"USD\".");
        }

        if (!TryText(root, "region", out var region))
        {
            return Invalid("\"region\" is missing.");
        }

        if (!TryText(root, "source", out var source))
        {
            return Invalid("\"source\" note is missing: an estimate with no source cannot be re-checked.");
        }

        if (!root.TryGetProperty("disk", out var disk) || disk.ValueKind != JsonValueKind.Object
            || !TryPositive(disk, "sizeGb", out var diskGb) || !TryPositive(disk, "usdPerGbMonth", out var diskPrice))
        {
            return Invalid("\"disk\" needs a positive \"sizeGb\" and \"usdPerGbMonth\".");
        }

        if (!root.TryGetProperty("machines", out var machines) || machines.ValueKind != JsonValueKind.Object)
        {
            return Invalid("\"machines\" is missing.");
        }

        var prices = new Dictionary<string, MachinePrice>(StringComparer.Ordinal);
        foreach (var machine in machines.EnumerateObject())
        {
            var problem = ReadMachine(machine, out var price);
            if (problem is not null)
            {
                return Invalid(problem);
            }

            prices[machine.Name] = price!;
        }

        return prices.Count == 0
            ? Invalid("\"machines\" lists no machine.")
            : new PricingLoadResult(new PricingTable(asOf, region, source, diskGb, diskPrice, prices), null, null);
    }

    private static string? ReadMachine(JsonProperty machine, out MachinePrice? price)
    {
        price = null;
        var value = machine.Value;
        if (value.ValueKind != JsonValueKind.Object
            || !TryText(value, "accelerator", out var accelerator)
            || !TryPositive(value, "onDemandUsdPerHour", out var onDemand))
        {
            return $"Machine \"{machine.Name}\" needs an \"accelerator\" and a positive \"onDemandUsdPerHour\".";
        }

        double? spot = null;
        if (value.TryGetProperty("spotUsdPerHour", out var spotElement) && spotElement.ValueKind != JsonValueKind.Null)
        {
            if (!TryPositive(value, "spotUsdPerHour", out var spotValue) || spotValue > onDemand)
            {
                return $"Machine \"{machine.Name}\" has a spot price that is not positive or is above its on-demand price.";
            }

            spot = spotValue;
        }

        price = new MachinePrice(machine.Name, accelerator, onDemand, spot);
        return null;
    }

    private static PricingLoadResult Invalid(string detail) => PricingLoadResult.Fail(PricingProblem.InvalidValue, detail);

    private static bool TryText(JsonElement element, string name, out string text)
    {
        text = string.Empty;
        if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString()))
        {
            text = value.GetString()!;
            return true;
        }

        return false;
    }

    /// <summary>A JSON number (never text) that is finite and above zero.</summary>
    private static bool TryPositive(JsonElement element, string name, out double number)
    {
        number = 0;
        return element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out number)
            && double.IsFinite(number)
            && number > 0;
    }
}
