namespace DnaEntropyGraph.Core.Inputs;

/// <summary>
/// The few Python <c>str</c> behaviours the worker's readers rely on, where .NET differs
/// (worker <c>readers/fasta.py</c>, <c>readers/detect.py</c>, <c>validation/validators.py</c>).
/// MEASURED 2026-10-02 against the real worker: a FASTA header ended by a form feed, vertical
/// tab, U+0085 or U+2028 is two lines there; <c>strip()</c> removes U+001C..U+001F; .NET's
/// <see cref="char.IsWhiteSpace(char)" /> does neither. Kept in one place so the C# readers
/// cannot drift from each other on this.
/// </summary>
internal static class PythonText
{
    /// <summary>Python <c>str.isspace()</c> for one UTF-16 unit: Unicode white space plus U+001C..U+001F.</summary>
    public static bool IsSpace(char c) => char.IsWhiteSpace(c) || c is >= '\u001c' and <= '\u001f';

    /// <summary>Python <c>s[start:end]</c> for non-negative <paramref name="start" />; a negative <paramref name="end" /> counts from the end. Never throws.</summary>
    public static string Slice(string s, int start, int end)
    {
        var to = Math.Min(end < 0 ? s.Length + end : end, s.Length);
        return start >= to ? string.Empty : s[start..to];
    }

    /// <summary>
    /// Python <c>ascii(s)</c>: a quoted literal that is pure ASCII (a non-ASCII code point becomes <c>\xNN</c>, <c>\uNNNN</c> or
    /// <c>\UNNNNNNNN</c>). Single quotes unless the text holds a single quote and no double quote.
    /// </summary>
    public static string Ascii(string s)
    {
        var quote = s.Contains('\'') && !s.Contains('"') ? '"' : '\'';
        var sb = new System.Text.StringBuilder().Append(quote);
        foreach (var rune in s.EnumerateRunes())
        {
            var v = rune.Value;
            if (v == quote || v == '\\')
            {
                sb.Append('\\').Append((char)v);
            }
            else if (v == '\t')
            {
                sb.Append("\\t");
            }
            else if (v == '\n')
            {
                sb.Append("\\n");
            }
            else if (v == '\r')
            {
                sb.Append("\\r");
            }
            else if (v is >= 0x20 and < 0x7f)
            {
                sb.Append((char)v);
            }
            else if (v <= 0xff)
            {
                sb.Append("\\x").Append(v.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
            }
            else if (v <= 0xffff)
            {
                sb.Append("\\u").Append(v.ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
            }
            else
            {
                sb.Append("\\U").Append(v.ToString("x8", System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        return sb.Append(quote).ToString();
    }

    /// <summary>Characters Python's <c>str.splitlines()</c> treats as a line boundary (besides CR LF as one).</summary>
    private static bool IsLineBreak(char c) =>
        c is '\n' or '\r' or '\u000b' or '\u000c' or '\u001c' or '\u001d' or '\u001e' or '\u0085' or '\u2028' or '\u2029';

    /// <summary>Python <c>str.splitlines()</c>: no trailing empty element for a final terminator; CR LF is one break.</summary>
    public static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (!IsLineBreak(text[i]))
            {
                continue;
            }
            lines.Add(text[start..i]);
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                i++;
            }
            start = i + 1;
        }
        if (start < text.Length)
        {
            lines.Add(text[start..]);
        }
        return lines;
    }

    public static string Trim(string s) => TrimEnd(TrimStart(s));

    public static string TrimStart(string s)
    {
        var i = 0;
        while (i < s.Length && IsSpace(s[i]))
        {
            i++;
        }
        return i == 0 ? s : s[i..];
    }

    public static string TrimEnd(string s)
    {
        var n = s.Length;
        while (n > 0 && IsSpace(s[n - 1]))
        {
            n--;
        }
        return n == s.Length ? s : s[..n];
    }

    /// <summary>The first whitespace-delimited word of an already-trimmed string (Python <c>s.split(None, 1)[0]</c>).</summary>
    public static string FirstWord(string trimmed)
    {
        var i = 0;
        while (i < trimmed.Length && !IsSpace(trimmed[i]))
        {
            i++;
        }
        return trimmed[..i];
    }
}
