using System.Globalization;

namespace DnaEntropyGraph.Core.Palettes;

public enum ColorVision
{
    Normal,
    Deuteranopia,
    Protanopia,
    Tritanopia,
}

/// <summary>
/// The backgrounds the tracks are drawn on: the viewer's own <c>viewer.css</c> and <c>dark.css</c> (a Guards test pins them to these). In the dark
/// theme the viewer's tracks do not sit on <see cref="Dark"/>: the dark filter is on <c>#igv</c>, whose canvases are white, so they sit on the
/// filtered white, <c>ViewerDarkFilter.Apply(Light)</c>. <see cref="Dark"/> is the page body behind it and the app's own dark surfaces.
/// </summary>
public static class PaletteBackgrounds
{
    public const string Light = "#FFFFFF";
    public const string Dark = "#1F1F1F";
}

/// <summary>
/// The CSS filter <c>invert(0.9) hue-rotate(180deg)</c> that <c>dark.css</c> puts over the viewer, per the Filter Effects spec (sRGB, channels 0 to 1,
/// the invert first). A Guards test pins the CSS to this wording, so the simulation cannot drift from the page.
/// </summary>
public static class ViewerDarkFilter
{
    public const string CssFilter = "invert(0.9) hue-rotate(180deg)";

    private static readonly double[][] HueRotate180 =
    [
        [-0.574, 1.430, 0.144],
        [0.426, 0.430, 0.144],
        [0.426, 1.430, -0.856],
    ];

    public static string Apply(string hex)
    {
        var (r, g, b) = PaletteValidator.ParseHex(hex);
        double[] inverted = [0.9 - (0.8 * r / 255.0), 0.9 - (0.8 * g / 255.0), 0.9 - (0.8 * b / 255.0)];
        var o = new int[3];
        for (var i = 0; i < 3; i++)
        {
            var v = (HueRotate180[i][0] * inverted[0]) + (HueRotate180[i][1] * inverted[1]) + (HueRotate180[i][2] * inverted[2]);
            o[i] = (int)Math.Round(Math.Clamp(v, 0, 1) * 255.0, MidpointRounding.AwayFromZero);
        }

        return string.Create(CultureInfo.InvariantCulture, $"#{o[0]:X2}{o[1]:X2}{o[2]:X2}");
    }

    public static PaletteColors Apply(PaletteColors colors)
        => new(Apply(colors.Entropy), Apply(colors.Forward), Apply(colors.Reverse), Apply(colors.Smoothed), Apply(colors.Marker));
}

/// <summary>
/// Checks a palette (issue #195): contrast of each colour against a background (WCAG 2.x relative luminance) and the distance between
/// every pair of colours as seen under deuteranopia, protanopia and tritanopia.
/// Simulation: Machado, Oliveira and Fernandes (2009), "A Physiologically-based Model for Simulation of Color Vision Deficiency",
/// IEEE TVCG 15(6), severity 1.0 matrices, applied to linear sRGB. Distance: CIEDE2000, Sharma, Wu and Dalal (2005), "The CIEDE2000
/// Color-Difference Formula: Implementation Notes", Color Res. Appl. 30(1) (D65 white, Lab through CIE XYZ).
/// </summary>
public static class PaletteValidator
{
    private static readonly double[][] Deuteranopia =
    [
        [0.367322, 0.860646, -0.227968],
        [0.280085, 0.672501, 0.047413],
        [-0.011820, 0.042940, 0.968881],
    ];

    private static readonly double[][] Protanopia =
    [
        [0.152286, 1.052583, -0.204868],
        [0.114503, 0.786281, 0.099216],
        [-0.003882, -0.048116, 1.051998],
    ];

    private static readonly double[][] Tritanopia =
    [
        [1.255528, -0.076749, -0.178779],
        [-0.078411, 0.930809, 0.147602],
        [0.004733, 0.691367, 0.303900],
    ];

    public static (int R, int G, int B) ParseHex(string hex)
    {
        if (hex.Length != 7 || hex[0] != '#')
        {
            throw new FormatException($"Not a #RRGGBB colour: {hex}");
        }

        return (
            int.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            int.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            int.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }

    private static double[] Linear(string hex)
    {
        var (r, g, b) = ParseHex(hex);
        return [Lin(r / 255.0), Lin(g / 255.0), Lin(b / 255.0)];
    }

    private static double Lin(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

    private static double Luminance(double[] linear) => (0.2126 * linear[0]) + (0.7152 * linear[1]) + (0.0722 * linear[2]);

    /// <summary>WCAG contrast ratio, 1 to 21.</summary>
    public static double ContrastRatio(string a, string b)
    {
        var la = Luminance(Linear(a));
        var lb = Luminance(Linear(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double[] Simulate(double[] linear, ColorVision vision)
    {
        var m = vision switch
        {
            ColorVision.Deuteranopia => Deuteranopia,
            ColorVision.Protanopia => Protanopia,
            ColorVision.Tritanopia => Tritanopia,
            _ => null,
        };
        if (m is null)
        {
            return linear;
        }

        var o = new double[3];
        for (var i = 0; i < 3; i++)
        {
            o[i] = Math.Clamp((m[i][0] * linear[0]) + (m[i][1] * linear[1]) + (m[i][2] * linear[2]), 0, 1);
        }

        return o;
    }

    private static (double L, double A, double B) ToLab(double[] c)
    {
        var x = ((0.4124564 * c[0]) + (0.3575761 * c[1]) + (0.1804375 * c[2])) / 0.95047;
        var y = (0.2126729 * c[0]) + (0.7151522 * c[1]) + (0.0721750 * c[2]);
        var z = ((0.0193339 * c[0]) + (0.1191920 * c[1]) + (0.9503041 * c[2])) / 1.08883;
        static double F(double t) => t > 216.0 / 24389.0 ? Math.Cbrt(t) : (((24389.0 / 27.0) * t) + 16.0) / 116.0;
        var fx = F(x);
        var fy = F(y);
        var fz = F(z);
        return ((116 * fy) - 16, 500 * (fx - fy), 200 * (fy - fz));
    }

    /// <summary>CIEDE2000 between two colours as a viewer with <paramref name="vision"/> sees them. 0 is identical; about 2.3 is one just-noticeable difference.</summary>
    public static double DeltaE2000(string a, string b, ColorVision vision)
    {
        return DeltaE2000FromLab(ToLab(Simulate(Linear(a), vision)), ToLab(Simulate(Linear(b), vision)));
    }

    /// <summary>CIEDE2000 between two CIE L*a*b* colours, the formula as published (Sharma, Wu and Dalal 2005); the test data in that paper is given in Lab.</summary>
    public static double DeltaE2000FromLab((double L, double A, double B) first, (double L, double A, double B) second)
    {
        var (l1, a1, b1) = first;
        var (l2, a2, b2) = second;

        var c1 = Math.Sqrt((a1 * a1) + (b1 * b1));
        var c2 = Math.Sqrt((a2 * a2) + (b2 * b2));
        var cBar = (c1 + c2) / 2;
        var cBar7 = Math.Pow(cBar, 7);
        var g = 0.5 * (1 - Math.Sqrt(cBar7 / (cBar7 + Math.Pow(25, 7))));
        var a1p = (1 + g) * a1;
        var a2p = (1 + g) * a2;
        var c1p = Math.Sqrt((a1p * a1p) + (b1 * b1));
        var c2p = Math.Sqrt((a2p * a2p) + (b2 * b2));
        var h1 = Hue(b1, a1p);
        var h2 = Hue(b2, a2p);

        var dL = l2 - l1;
        var dC = c2p - c1p;
        double dh = 0;
        if (c1p * c2p != 0)
        {
            dh = h2 - h1;
            if (dh > 180)
            {
                dh -= 360;
            }
            else if (dh < -180)
            {
                dh += 360;
            }
        }

        var dH = 2 * Math.Sqrt(c1p * c2p) * Math.Sin(Rad(dh) / 2);
        var lBar = (l1 + l2) / 2;
        var cBarP = (c1p + c2p) / 2;
        double hBar;
        if (c1p * c2p == 0)
        {
            hBar = h1 + h2;
        }
        else if (Math.Abs(h1 - h2) <= 180)
        {
            hBar = (h1 + h2) / 2;
        }
        else if (h1 + h2 < 360)
        {
            hBar = (h1 + h2 + 360) / 2;
        }
        else
        {
            hBar = (h1 + h2 - 360) / 2;
        }

        var t = 1 - (0.17 * Math.Cos(Rad(hBar - 30))) + (0.24 * Math.Cos(Rad(2 * hBar))) + (0.32 * Math.Cos(Rad((3 * hBar) + 6))) - (0.20 * Math.Cos(Rad((4 * hBar) - 63)));
        var dTheta = 30 * Math.Exp(-Math.Pow((hBar - 275) / 25, 2));
        var cBarP7 = Math.Pow(cBarP, 7);
        var rc = 2 * Math.Sqrt(cBarP7 / (cBarP7 + Math.Pow(25, 7)));
        var sl = 1 + (0.015 * Math.Pow(lBar - 50, 2) / Math.Sqrt(20 + Math.Pow(lBar - 50, 2)));
        var sc = 1 + (0.045 * cBarP);
        var sh = 1 + (0.015 * cBarP * t);
        var rt = -Math.Sin(Rad(2 * dTheta)) * rc;
        var termL = dL / sl;
        var termC = dC / sc;
        var termH = dH / sh;
        return Math.Sqrt((termL * termL) + (termC * termC) + (termH * termH) + (rt * termC * termH));
    }

    private static double Rad(double degrees) => degrees * Math.PI / 180.0;

    private static double Hue(double b, double aPrime)
    {
        var h = Math.Atan2(b, aPrime) * 180.0 / Math.PI;
        return h < 0 ? h + 360 : h;
    }

    /// <summary>
    /// Every way <paramref name="colors"/> falls short, one sentence each (empty when it passes): a colour with less than
    /// <paramref name="minContrast"/> against <paramref name="background"/>, or a pair of roles closer than <paramref name="minDeltaE"/>
    /// under normal vision, deuteranopia, protanopia or tritanopia.
    /// </summary>
    public static IReadOnlyList<string> Validate(PaletteColors colors, string background, double minContrast, double minDeltaE)
    {
        var problems = new List<string>();
        var roles = Enum.GetValues<TrackRole>();
        foreach (var role in roles)
        {
            var ratio = ContrastRatio(colors.For(role), background);
            if (ratio < minContrast)
            {
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"{role} {colors.For(role)} has contrast {ratio:0.00} against the background {background}, below {minContrast}."));
            }
        }

        for (var i = 0; i < roles.Length; i++)
        {
            for (var j = i + 1; j < roles.Length; j++)
            {
                foreach (var vision in Enum.GetValues<ColorVision>())
                {
                    var d = DeltaE2000(colors.For(roles[i]), colors.For(roles[j]), vision);
                    if (d < minDeltaE)
                    {
                        problems.Add(string.Create(CultureInfo.InvariantCulture, $"{roles[i]} and {roles[j]} are only {d:0.0} apart under {vision}, below {minDeltaE}."));
                    }
                }
            }
        }

        return problems;
    }
}
