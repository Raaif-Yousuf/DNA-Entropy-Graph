using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace DnaEntropyGraph.Core.Diagnostics;

/// <summary>
/// Routes <see cref="Trace"/> output (the app's <c>Trace.TraceWarning/TraceError/TraceInformation</c> calls, issue #923) into
/// <c>&lt;root&gt;\logs\app.log</c>, the same file <see cref="Inputs.FileDiagnosticsLog"/> writes and the diagnostics zip collects.
/// Every call site in <c>app/src</c> passes only constant text plus an exception type name (a Guards test holds that), and every line is
/// redacted again here (by <see cref="DiagnosticsRedactor"/>, the zip's own rules) because third-party code can also write to Trace: absolute paths, email addresses, tokens and long nucleotide runs become
/// placeholders. Never throws into the caller.
/// <para>Decision (#923): in a Debug build <c>Debug.WriteLine</c> output reaches this file too, because <see cref="Debug"/> and <see cref="Trace"/> share one listener
/// collection. Kept, not filtered: the line is redacted here, again by the diagnostics bundle builder, and checked by its leak scan, and installing
/// only in Release would hide the observable from a dev build.</para>
/// </summary>
public sealed class DiagnosticsTraceListener : TraceListener
{
    // One source of truth for what a log may not hold (paths, emails, tokens, sequences, sequence file names): the same rules the diagnostics zip applies.
    private static readonly DiagnosticsRedactor Redactor = new(string.Empty, []);

    private readonly AppLogFile _file;
    private readonly TimeProvider _time;
    /// <summary>Largest unfinished <c>Write</c> fragment kept before it is emitted as a line of its own.</summary>
    public const int MaxPendingChars = 4096;

    // Per thread, so a half line one thread wrote with Write can never be glued onto a line another thread finishes with WriteLine.
    [ThreadStatic]
    private static StringBuilder? _pending;

    public DiagnosticsTraceListener(string root, TimeProvider? time = null, long maxBytes = AppLogFile.DefaultMaxBytes)
    {
        _file = new AppLogFile(root, maxBytes);
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// The startup registration (called from <c>Program.Main</c> once the data folder is known). Idempotent: an earlier instance is removed first,
    /// because <see cref="Debug"/> and <see cref="Trace"/> share one listener collection and a double registration writes every line twice.
    /// </summary>
    public static void Install(string root, TimeProvider? time = null)
    {
        Uninstall();
        Trace.Listeners.Add(new DiagnosticsTraceListener(root, time));
        Trace.AutoFlush = true;
    }

    /// <summary>Removes every instance from the shared listener collection (tests; the app never calls this).</summary>
    public static void Uninstall()
    {
        foreach (var existing in Trace.Listeners.OfType<DiagnosticsTraceListener>().ToList())
        {
            Trace.Listeners.Remove(existing);
            existing.Dispose();
        }
    }

    /// <summary>The line hygiene and redaction every Trace line gets, exposed so the rules are testable on their own.</summary>
    public static string Redact(string text)
    {
        return Redactor.RedactText(text);
    }

    public override void Write(string? message)
    {
        if (message is null)
        {
            return;
        }

        var pending = _pending ??= new StringBuilder();
        pending.Append(message);
        if (pending.Length >= MaxPendingChars)
        {
            // A Write that never meets its WriteLine must not grow without bound: emit what is there as a line.
            Flush("INFO");
        }
    }

    public override void WriteLine(string? message)
    {
        _pending ??= new StringBuilder();
        _pending.Append(message);
        Flush("INFO");
    }

    private void Flush(string level)
    {
        var text = _pending!.ToString();
        _pending.Clear();
        Emit(level, text);
    }
    public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? message)
    {
        if (Filter is null || Filter.ShouldTrace(eventCache, source, eventType, id, message, null, null, null))
        {
            Emit(Level(eventType), message ?? string.Empty);
        }
    }

    public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? format, params object?[]? args)
    {
        if (Filter is not null && !Filter.ShouldTrace(eventCache, source, eventType, id, format, args, null, null))
        {
            return;
        }

        string message;
        try
        {
            message = args is { Length: > 0 } && format is not null
                ? string.Format(CultureInfo.InvariantCulture, format, args)
                : format ?? string.Empty;
        }
        catch (FormatException)
        {
            message = format ?? string.Empty;
        }

        Emit(Level(eventType), message);
    }

    public override void TraceData(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, object? data)
        => Emit(Level(eventType), data is null ? string.Empty : "data " + data.GetType().Name);

    public override void TraceData(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, params object?[]? data)
        => Emit(Level(eventType), "data " + string.Join(' ', (data ?? []).Select(d => d?.GetType().Name ?? "null")));

    private static string Level(TraceEventType type) => type switch
    {
        TraceEventType.Critical or TraceEventType.Error => "ERROR",
        TraceEventType.Warning => "WARN",
        _ => "INFO",
    };

    private void Emit(string level, string text)
    {
        try
        {
            var line = string.Create(
                CultureInfo.InvariantCulture,
                $"{_time.GetUtcNow():yyyy-MM-ddTHH:mm:ss.fffZ} {level} trace {Redact(AppLogFile.Clean(text))}");
            _file.AppendLine(line);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
        }
    }
}
