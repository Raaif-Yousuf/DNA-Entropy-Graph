using System.Text.Json;

namespace DnaEntropyGraph.Core.Contract;

/// <summary>One file the worker uploaded; <see cref="Sha256"/> and <see cref="Bytes"/> are null when only a bare <c>outputs</c> path was listed.</summary>
public sealed record WorkerResultFile(string Path, string? Sha256, long? Bytes);

/// <summary>One input's outcome in <c>result.json</c>.</summary>
public sealed record WorkerInputResult(string Id, string Status, IReadOnlyList<WorkerResultFile> Files, string? ErrorCode, string? ErrorMessage);

/// <summary>
/// <c>result.json</c> as the app reads it (docs/job_contract.md section 7).
/// <see cref="Status"/> is <c>done | failed | cancelled</c> for the job as a whole.
/// </summary>
public sealed record WorkerResultDocument(string Status, IReadOnlyList<WorkerInputResult> Inputs, string? ErrorCode, string? ErrorMessage);

/// <summary>
/// Reads <c>result.json</c>. Strict on purpose: the file existing is the
/// worker's terminal signal, so a file that does not say <c>done</c>, <c>failed</c>
/// or <c>cancelled</c> must never be read as success.
/// </summary>
public static class WorkerResultReader
{
    private static readonly HashSet<string> JobStatuses = new(StringComparer.Ordinal) { "done", "failed", "cancelled" };

    /// <summary>Throws <see cref="InvalidDataException"/> for anything that is not a result this app understands.</summary>
    public static WorkerResultDocument Parse(string json)
    {
        try
        {
            return ParseCore(json);
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or OverflowException)
        {
            // A number where text was expected, an integer too large, and similar: still "not a result we understand".
            throw new InvalidDataException("result.json has a field of an unexpected type.", ex);
        }
    }

    /// <summary>
    /// The <c>error.code</c> in a <c>status.json</c> (docs/job_contract.md section 4), or null when there is
    /// none, the text is not JSON, or the field is not a string. Lenient on purpose: this is a diagnostic read
    /// of a file the VM's startup script wrote while it was failing, never a reason to fail a run itself.
    /// </summary>
    public static string? TryReadStatusErrorCode(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("error", out var error)
                   && error.ValueKind == JsonValueKind.Object
                   && error.TryGetProperty("code", out var code)
                   && code.ValueKind == JsonValueKind.String
                ? code.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static WorkerResultDocument ParseCore(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("result.json is not valid JSON.", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("result.json is not a JSON object.");
            }

            if (!root.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.Number || schema.GetInt32() != 1)
            {
                throw new InvalidDataException("result.json has a schema this app does not understand.");
            }

            var status = StringOrNull(root, "status");
            if (status is null || !JobStatuses.Contains(status))
            {
                throw new InvalidDataException("result.json has no recognised job status.");
            }

            var inputs = new List<WorkerInputResult>();
            if (root.TryGetProperty("inputs", out var inputArray) && inputArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var input in inputArray.EnumerateArray())
                {
                    inputs.Add(ReadInput(input));
                }
            }
            else if (status == "done")
            {
                throw new InvalidDataException("result.json says done but lists no inputs array.");
            }

            var (code, message) = ReadError(root);
            return new WorkerResultDocument(status, inputs, code, message);
        }
    }

    private static WorkerInputResult ReadInput(JsonElement input)
    {
        var files = new List<WorkerResultFile>();
        var listed = new HashSet<string>(StringComparer.Ordinal);
        if (input.TryGetProperty("files", out var fileArray) && fileArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var file in fileArray.EnumerateArray())
            {
                var path = StringOrNull(file, "path");
                if (path is null)
                {
                    continue;
                }

                long? bytes = file.TryGetProperty("bytes", out var b) && b.ValueKind == JsonValueKind.Number ? b.GetInt64() : null;
                files.Add(new WorkerResultFile(path, StringOrNull(file, "sha256"), bytes));
                listed.Add(path);
            }
        }

        // A bare outputs path with no files entry is still downloadable, just unverifiable.
        if (input.TryGetProperty("outputs", out var outputArray) && outputArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var output in outputArray.EnumerateArray())
            {
                if (output.ValueKind == JsonValueKind.String && output.GetString() is { } path && listed.Add(path))
                {
                    files.Add(new WorkerResultFile(path, null, null));
                }
            }
        }

        var (code, message) = ReadError(input);
        return new WorkerInputResult(StringOrNull(input, "id") ?? string.Empty, StringOrNull(input, "status") ?? string.Empty, files, code, message);
    }

    private static (string? Code, string? Message) ReadError(JsonElement owner)
        => owner.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
            ? (StringOrNull(error, "code"), StringOrNull(error, "message"))
            : (null, null);

    private static string? StringOrNull(JsonElement owner, string name)
        => owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
