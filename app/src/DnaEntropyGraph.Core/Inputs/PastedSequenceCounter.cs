namespace DnaEntropyGraph.Core.Inputs;

/// <summary>
/// The live "N bases" counter under the Paste sequence box (issue #63). It counts what the validator will see:
/// letters, with whitespace, digits and FASTA header lines (a line starting with <c>&gt;</c>) left out.
/// </summary>
public static class PastedSequenceCounter
{
    public static int Count(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var count = 0;
        var atLineStart = true;
        var inHeader = false;
        foreach (var c in text)
        {
            if (c is '\n' or '\r')
            {
                atLineStart = true;
                inHeader = false;
                continue;
            }

            if (atLineStart && !char.IsWhiteSpace(c))
            {
                inHeader = c == '>';
                atLineStart = false;
            }
            else if (atLineStart)
            {
                continue;
            }

            if (!inHeader && char.IsLetter(c))
            {
                count++;
            }
        }

        return count;
    }
}
