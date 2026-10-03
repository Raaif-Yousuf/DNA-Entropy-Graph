using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DnaEntropyGraph.Cloud.Auth;

/// <summary>
/// One account in <c>accounts.json</c>. No token, ever: the token is in the DPAPI file keyed by <see cref="Sub"/>.
/// <see cref="ProjectId"/> is the project the account chose in the wizard (issue #520); a file written before it existed has none.
/// </summary>
public sealed record AccountRecord(
    [property: JsonPropertyName("sub")] string Sub,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("needsSignIn")] bool NeedsSignIn,
    [property: JsonPropertyName("projectId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ProjectId = null);

/// <summary>The switcher's list and which account is current.</summary>
public sealed record AccountsFile(
    [property: JsonPropertyName("activeSub")] string? ActiveSub,
    [property: JsonPropertyName("accounts")] IReadOnlyList<AccountRecord> Accounts)
{
    public static AccountsFile Empty { get; } = new(null, []);

    public AccountRecord? Active => ActiveSub is null ? null : Accounts.FirstOrDefault(a => a.Sub == ActiveSub);
}

/// <summary><c>accounts.json</c> beside the token files: what the switcher lists, as plain JSON (UTF-8, LF), written atomically.</summary>
public sealed class AccountRegistry
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, NewLine = "\n" };

    private static readonly TimeSpan LockWait = TimeSpan.FromSeconds(10);

    private readonly string _path;

    public AccountRegistry(string directory)
    {
        _path = Path.Combine(directory, "accounts.json");
    }

    /// <summary>Where the last <see cref="Load"/> set an unparseable file aside (<c>accounts.json.bad</c>, or <c>.bad.1</c>, <c>.bad.2</c> and so on, never over an earlier one), or null when it set nothing aside.</summary>
    public string? QuarantinedTo { get; private set; }

    /// <summary>
    /// A missing file is an empty list (a first run). A file that does not parse is also an empty list, but the file is first moved aside
    /// (<see cref="QuarantinedTo"/>) so the next <see cref="Save"/> cannot overwrite the only copy of the user's account list (issue #616).
    /// A file that cannot be read at all (locked, no access) is an empty list and is left where it is.
    /// </summary>
    public AccountsFile Load()
    {
        QuarantinedTo = null;
        if (!File.Exists(_path))
        {
            return AccountsFile.Empty;
        }

        string text;
        try
        {
            text = File.ReadAllText(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return AccountsFile.Empty;
        }

        if (TryParse(text) is { } file)
        {
            return file;
        }

        // Under the same lock as Save: a parse failure is real only if no writer was mid-replace (the replace is atomic, so it is), and the move must not race a Save.
        try
        {
            WithLock(() => QuarantinedTo = MoveAside());
        }
        catch (TokenStorageException)
        {
            // Could not move it aside: leave it. Nothing here writes, and a later Save is the caller's to refuse.
        }

        return AccountsFile.Empty;
    }

    /// <summary>Replaces the file atomically under a machine-wide named mutex, so two processes (or two instances) saving at once serialise instead of colliding on one temp file.</summary>
    public void Save(AccountsFile file)
        => WithLock(() =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + "." + Guid.NewGuid().ToString("n") + ".tmp";
            try
            {
                File.WriteAllText(temp, JsonSerializer.Serialize(file, Json) + "\n", new UTF8Encoding(false));
                File.Move(temp, _path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
        });

    private static AccountsFile? TryParse(string text)
    {
        try
        {
            return JsonSerializer.Deserialize<AccountsFile>(text, Json) is { Accounts: not null } file ? file : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private string? MoveAside()
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        for (var n = 0; ; n++)
        {
            var target = n == 0 ? _path + ".bad" : _path + ".bad." + n;
            if (!File.Exists(target))
            {
                File.Move(_path, target);
                return target;
            }
        }
    }

    private void WithLock(Action action)
    {
        var name = @"Local\DnaEntropyGraph.accounts." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(_path).ToUpperInvariant())), 0, 8);
        try
        {
            using var mutex = new Mutex(false, name);
            var held = false;
            try
            {
                held = mutex.WaitOne(LockWait);
            }
            catch (AbandonedMutexException)
            {
                // The previous owner died holding it. The file is replaced atomically, so it is whole: carry on.
                held = true;
            }

            if (!held)
            {
                throw new IOException("another copy of the app held the accounts file lock for too long");
            }

            try
            {
                action();
            }
            finally
            {
                mutex.ReleaseMutex();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new TokenStorageException(ex);
        }
    }
}
