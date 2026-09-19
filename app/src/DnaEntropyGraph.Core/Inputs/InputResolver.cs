namespace DnaEntropyGraph.Core.Inputs;

/// <summary>What the drop zone / paste box decided a piece of user input means.</summary>
public enum InputResolutionKind
{
    /// <summary>An existing file - a real drop, or typed/pasted text that resolved to a file on disk.</summary>
    ExistingFile,

    /// <summary>The text looks like a path (contains a separator or a drive letter) but no such file exists.</summary>
    PathNotFound,

    /// <summary>Not a path at all - treated as a pasted sequence.</summary>
    PastedSequence,

    /// <summary>Nothing was entered (empty/whitespace-only).</summary>
    Empty,
}

/// <summary>
/// The outcome of resolving one piece of user input (a drop, a typed path, or pasted text)
/// into what the user meant. A plain data shape a ViewModel can bind to directly - no
/// exceptions, no console I/O, no user-visible strings (Hard Rule 13: those live in
/// <c>Resources.resw</c>, keyed by <see cref="Kind" /> and formatted with
/// <see cref="AttemptedPath" />/<see cref="SequenceText" /> by the caller).
/// </summary>
public sealed record InputResolution
{
    public required InputResolutionKind Kind { get; init; }

    /// <summary>Set when <see cref="Kind" /> is <see cref="InputResolutionKind.ExistingFile" />.</summary>
    public string? FilePath { get; init; }

    /// <summary>Set when <see cref="Kind" /> is <see cref="InputResolutionKind.PathNotFound" /> - the text as typed/pasted, quotes already stripped.</summary>
    public string? AttemptedPath { get; init; }

    /// <summary>Set when <see cref="Kind" /> is <see cref="InputResolutionKind.PastedSequence" /> - the raw pasted text, unmodified (validation happens downstream via <see cref="SequenceValidator" />).</summary>
    public string? SequenceText { get; init; }
}

/// <summary>
/// Decides what a dropped file, a typed/pasted path, or a pasted DNA sequence means - the
/// C# port of the prototype's double-click wizard (issue #211,
/// <c>DNA-Entropy-Genbank/packaging/launcher.py::_resolve_input</c>, deleted from this
/// repo's worker under issue #291 once the WinUI app became the entry point; the rule it
/// encoded still has no C# home, which is exactly this class). The paste dialog and the
/// drop zone call the SAME method, so they behave identically (issue #211's own "Why").
///
/// This is pure decision logic: it checks whether the text names a real file
/// (<see cref="File.Exists(string)" />), but it never reads, writes, or validates a
/// sequence itself - that is <see cref="SequenceValidator" />'s and the caller's job.
/// </summary>
public static class InputResolver
{
    /// <summary>
    /// Resolve <paramref name="raw" /> (a drop's path, or the paste box's text) into an
    /// <see cref="InputResolution" />.
    ///
    /// Mirrors <c>_resolve_input</c> exactly:
    /// 1. Strip whitespace, then any number of surrounding <c>"</c> characters, then any
    ///    number of surrounding <c>'</c> characters, then whitespace again (a path pasted
    ///    from Windows Explorer's "Copy as path" is wrapped in double quotes).
    /// 2. Empty after stripping -&gt; <see cref="InputResolutionKind.Empty" />.
    /// 3. An existing file -&gt; <see cref="InputResolutionKind.ExistingFile" />.
    /// 4. "Looks like a path" (contains <c>\</c> or <c>/</c>, or looks like a drive letter,
    ///    e.g. <c>C:</c>) but does not exist -&gt; <see cref="InputResolutionKind.PathNotFound" />,
    ///    so the user sees "file not found", not a confusing attempt to validate a
    ///    Windows path as a DNA sequence.
    /// 5. Otherwise -&gt; <see cref="InputResolutionKind.PastedSequence" />.
    /// </summary>
    public static InputResolution Resolve(string? raw)
    {
        var text = (raw ?? string.Empty).Trim();
        text = text.Trim('"');
        text = text.Trim('\'');
        text = text.Trim();

        if (text.Length == 0)
        {
            return new InputResolution { Kind = InputResolutionKind.Empty };
        }

        if (File.Exists(text))
        {
            return new InputResolution { Kind = InputResolutionKind.ExistingFile, FilePath = text };
        }

        var looksLikePath = text.Contains('\\') || text.Contains('/') || (text.Length > 1 && text[1] == ':');
        if (looksLikePath)
        {
            return new InputResolution { Kind = InputResolutionKind.PathNotFound, AttemptedPath = text };
        }

        return new InputResolution { Kind = InputResolutionKind.PastedSequence, SequenceText = text };
    }
}
