namespace DnaEntropyGraph.Core.Cost;

/// <summary>Where the app gets its price list. The real one reads the file that ships beside the app; a test hands back a table.</summary>
public interface IPricingSource
{
    /// <summary>Never throws: a missing, unreadable or invalid list is a <see cref="PricingLoadResult"/> with a problem.</summary>
    PricingLoadResult Load();
}

/// <summary>Reads <c>pricing.json</c> from disk (the app points it at <c>Assets\pricing.json</c> in its own output folder).</summary>
public sealed class FilePricingSource(string path) : IPricingSource
{
    public PricingLoadResult Load()
    {
        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return PricingLoadResult.Fail(PricingProblem.FileMissing, "No price list at " + path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return PricingLoadResult.Fail(PricingProblem.Unreadable, ex.GetType().Name + " reading " + path);
        }

        return PricingTable.Parse(json);
    }
}
