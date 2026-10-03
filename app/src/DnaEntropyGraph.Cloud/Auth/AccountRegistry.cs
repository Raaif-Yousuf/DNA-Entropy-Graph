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

    private const int ReadAttempts = 5;
    private const int ReadBackoffMs = 50;
    // A read gives up on the mutex quickly (it can run on the UI thread); a save may wait longer because it is the user's change.
    private static readonly TimeSpan ReadLockWait = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SaveLockWait = TimeSpan.FromSeconds(10);

    private readonly string _path;
    private readonly Func<string, Mutex> _openMutex;

    public AccountRegistry(string directory)
        : this(directory, name => new Mutex(false, name))
    {
    }

    /// <summary>Tests only: how the named mutex is opened, so a refusal by the system can be played.</summary>
    internal AccountRegistry(string directory, Func<string, Mutex> openMutex)
    {
        _path = Path.Combine(directory, "accounts.json");
        _openMutex = openMutex;
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
        Unreadable = false;
        var result = AccountsFile.Empty;
        try
        {
            // Under the same lock as Save, so a read never races another process's replace, and the move aside never races a Save.
            WithLock(() => result = ReadLocked(), ReadLockWait);
        }
        catch (TokenStorageException)
        {
            // The lock was never granted or the file could not be moved aside: the file is untouched and Save refuses until a Load succeeds.
            Unreadable = true;
        }

        return result;
    }

    /// <summary>
    /// True when the last <see cref="Load"/> found the file but could not read it (another program or copy of the app holds it) after retrying.
    /// The list is then empty only because it is unread, so <see cref="Save"/> refuses to replace the file until a later <see cref="Load"/> succeeds.
    /// </summary>
    public bool Unreadable { get; private set; }

    private AccountsFile ReadLocked()
    {
        if (!File.Exists(_path))
        {
            return AccountsFile.Empty;
        }

        string? text = null;
        for (var attempt = 1; attempt <= ReadAttempts && text is null; attempt++)
        {
            try
            {
                text = File.ReadAllText(_path);
            }
            catch (FileNotFoundException)
            {
                return AccountsFile.Empty;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == ReadAttempts)
                {
                    Unreadable = true;
                    return AccountsFile.Empty;
                }

                Thread.Sleep(ReadBackoffMs * attempt);
            }
        }

        if (TryParse(text!) is { } file)
        {
            return file;
        }

        try
        {
            QuarantinedTo = MoveAside();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Could not move it aside: the damaged file stays where it is and Save refuses.
            Unreadable = true;
        }

        return AccountsFile.Empty;
    }

    /// <summary>
    /// Replaces the file atomically under a named mutex in the <c>Local\</c> namespace (one Windows session, so every copy of the app one user runs;
    /// an elevated copy cannot be assumed to share it), so two processes or instances saving at once serialise instead of colliding on one temp file.
    /// Refuses while <see cref="Unreadable"/>.
    /// </summary>
    public void Save(AccountsFile file)
        => WithLock(() =>
        {
            if (Unreadable)
            {
                throw new IOException("the accounts file exists but could not be read, so it is not replaced");
            }

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
        }, SaveLockWait);

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

    private void WithLock(Action action, TimeSpan wait)
    {
        var name = @"Local\DnaEntropyGraph.accounts." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(_path).ToUpperInvariant())), 0, 8);
        try
        {
            using var mutex = _openMutex(name);
            var held = false;
            try
            {
                held = mutex.WaitOne(wait);
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
