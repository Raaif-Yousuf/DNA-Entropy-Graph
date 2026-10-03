using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DnaEntropyGraph.Cloud.Auth;

/// <summary>One account in <c>accounts.json</c>. No token, ever: the token is in the DPAPI file keyed by <see cref="Sub"/>.</summary>
public sealed record AccountRecord(
    [property: JsonPropertyName("sub")] string Sub,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("needsSignIn")] bool NeedsSignIn);

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

    private readonly string _path;

    public AccountRegistry(string directory)
    {
        _path = Path.Combine(directory, "accounts.json");
    }

    /// <summary>An absent or unreadable file is an empty list: the user signs in again, nothing is lost that was not already unreadable.</summary>
    public AccountsFile Load()
    {
        if (!File.Exists(_path))
        {
            return AccountsFile.Empty;
        }

        try
        {
            var file = JsonSerializer.Deserialize<AccountsFile>(File.ReadAllText(_path), Json);
            return file is { Accounts: not null } ? file : AccountsFile.Empty;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return AccountsFile.Empty;
        }
    }

    public void Save(AccountsFile file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(file, Json) + "\n", new UTF8Encoding(false));
        File.Move(temp, _path, overwrite: true);
    }
}
