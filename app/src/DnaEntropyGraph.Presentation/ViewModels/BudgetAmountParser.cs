using System.Globalization;

namespace DnaEntropyGraph.Presentation.ViewModels;

/// <summary>
/// Reads the amount box the way the user typed it. The user's own number format decides ("12,5" is 12.5 in Germany and France), a
/// thousands separator is refused, and so is any text that could mean two things ("1.000" in Germany): the money is never guessed.
/// </summary>
public static class BudgetAmountParser
{
    public static bool TryParse(string? text, CultureInfo culture, out decimal amount)
    {
        amount = 0;
        var trimmed = (text ?? string.Empty).Trim().TrimStart('$').Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        if (decimal.TryParse(trimmed, NumberStyles.AllowDecimalPoint, culture, out amount))
        {
            return true;
        }

        // The culture could not read it. A plain invariant "12.5" is accepted only when the text holds nothing this culture reads as a group
        // separator: in de-DE the group separator IS ".", so "12.5" (and "1.000") is refused as ambiguous; in fr-FR (a narrow space) "12.5" is accepted.
        // A text with a comma is never taken through the invariant path.
        var group = culture.NumberFormat.NumberGroupSeparator;
        if (group.Length > 0 && trimmed.Contains(group, StringComparison.Ordinal))
        {
            return false;
        }

        return decimal.TryParse(trimmed, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out amount)
            && !trimmed.Contains(',', StringComparison.Ordinal);
    }
}
