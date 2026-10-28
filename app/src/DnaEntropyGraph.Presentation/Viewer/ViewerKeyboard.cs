using System.Globalization;

namespace DnaEntropyGraph.Presentation.Viewer;

/// <summary>The window igv.js shows: record name and 1-based inclusive range, as <c>locuschange</c> reports it (bridge.js).</summary>
public readonly record struct ViewerLocus(string Chr, long Start, long End)
{
    public long Width => End - Start + 1;

    public override string ToString() => $"{Chr}:{Start.ToString(CultureInfo.InvariantCulture)}-{End.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>Reads <c>name:start-end</c>; the name may itself contain colons (the last one splits). False for anything else.</summary>
    public static bool TryParse(string? text, out ViewerLocus locus)
    {
        locus = default;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var colon = text.LastIndexOf(':');
        var dash = text.LastIndexOf('-');
        if (colon <= 0 || dash <= colon + 1 || dash >= text.Length - 1)
        {
            return false;
        }

        if (!long.TryParse(text.AsSpan(colon + 1, dash - colon - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var start)
            || !long.TryParse(text.AsSpan(dash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var end)
            || start < 1
            || end < start)
        {
            return false;
        }

        locus = new ViewerLocus(text[..colon], start, end);
        return true;
    }
}

public enum ViewerKeyKind
{
    None,
    Goto,
    FocusLocus,
}

public readonly record struct ViewerKeyResult(ViewerKeyKind Kind, string? Locus = null)
{
    public static readonly ViewerKeyResult Nothing = new(ViewerKeyKind.None);
}

/// <summary>
/// Issue #247: what a key pressed inside the viewer page does. bridge.js only forwards <c>KeyboardEvent.key</c>;
/// every decision (step sizes, clamping, presets) is here so it is tested without a browser. Arrow keys pan a quarter
/// of the window, + and - halve or double it about its centre (never below 40 bases), Home or 1 shows the whole record,
/// 2 and 3 show 10 kb and 1 kb centred on the window, Ctrl+L focuses the locus box.
/// </summary>
public static class ViewerKeyboard
{
    public const long MinWindow = 40;
    public const long TenKb = 10_000;
    public const long OneKb = 1_000;

    public static ViewerKeyResult Resolve(string? key, bool ctrl, ViewerLocus? current, int? recordLength)
    {
        if (ctrl)
        {
            return string.Equals(key, "l", StringComparison.OrdinalIgnoreCase)
                ? new ViewerKeyResult(ViewerKeyKind.FocusLocus)
                : ViewerKeyResult.Nothing;
        }

        if (current is not { } cur || recordLength is not { } len || len < 1)
        {
            return ViewerKeyResult.Nothing;
        }

        var width = cur.Width;
        long start;
        long newWidth;
        switch (key)
        {
            case "ArrowLeft":
                newWidth = width;
                start = cur.Start - Math.Max(1, width / 4);
                break;
            case "ArrowRight":
                newWidth = width;
                start = cur.Start + Math.Max(1, width / 4);
                break;
            case "+":
            case "=":
                newWidth = Math.Min(width, Math.Max(MinWindow, width / 2));
                start = cur.Start + ((width - newWidth) / 2);
                break;
            case "-":
            case "_":
                newWidth = width * 2;
                start = cur.Start - ((newWidth - width) / 2);
                break;
            case "Home":
            case "1":
                newWidth = len;
                start = 1;
                break;
            case "2":
                newWidth = TenKb;
                start = cur.Start + ((width - newWidth) / 2);
                break;
            case "3":
                newWidth = OneKb;
                start = cur.Start + ((width - newWidth) / 2);
                break;
            default:
                return ViewerKeyResult.Nothing;
        }

        newWidth = Math.Min(newWidth, len);
        start = Math.Clamp(start, 1, len - newWidth + 1);
        return new ViewerKeyResult(ViewerKeyKind.Goto, new ViewerLocus(cur.Chr, start, start + newWidth - 1).ToString());
    }
}
