using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Google.Apis.Json;
using Google.Apis.Util.Store;

namespace DnaEntropyGraph.Cloud.Auth;

/// <summary>
/// Google's token store contract over one encrypted file per key: <c>&lt;key&gt;.tok</c> in <see cref="Directory"/>
/// (issue #48). The key is the account's OpenID <c>sub</c>, so two Google accounts on one PC never share a file and
/// the same account on two PCs never collides (each PC has its own folder and its own DPAPI key). A missing,
/// undecryptable or unparseable file reads as "no token": the caller treats it as a sign-in that expired.
/// </summary>
public sealed partial class DpapiTokenStore : IDataStore
{
    private readonly string _directory;
    private readonly ISecretProtector _protector;

    public DpapiTokenStore(string directory, ISecretProtector protector)
    {
        _directory = directory;
        _protector = protector;
    }

    public string Directory => _directory;

    /// <summary>A key is used as a file name, so it is held to letters, digits, '-' and '_' (a Google sub is digits).</summary>
    public static bool IsSafeKey(string? key) => key is not null && SafeKey().IsMatch(key);

    public string PathFor(string key) => Path.Combine(_directory, RequireSafe(key) + ".tok");

    public Task StoreAsync<T>(string key, T value)
    {
        var path = PathFor(key);
        try
        {
            System.IO.Directory.CreateDirectory(_directory);
            var protectedBytes = _protector.Protect(Encoding.UTF8.GetBytes(NewtonsoftJsonSerializer.Instance.Serialize(value)));

            // Write beside, then replace: a crash mid-write must never leave a half-written token where a good one was.
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, protectedBytes);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            throw new TokenStorageException(ex);
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync<T>(string key)
    {
        var path = PathFor(key);
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new TokenStorageException(ex);
        }

        return Task.CompletedTask;
    }

    public Task<T> GetAsync<T>(string key)
    {
        var path = PathFor(key);
        if (!File.Exists(path))
        {
            return Task.FromResult(default(T)!);
        }

        try
        {
            var json = Encoding.UTF8.GetString(_protector.Unprotect(File.ReadAllBytes(path)));
            return Task.FromResult(NewtonsoftJsonSerializer.Instance.Deserialize<T>(json));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or IOException or UnauthorizedAccessException or Newtonsoft.Json.JsonException)
        {
            return Task.FromResult(default(T)!);
        }
    }

    public Task ClearAsync()
    {
        if (System.IO.Directory.Exists(_directory))
        {
            foreach (var file in System.IO.Directory.GetFiles(_directory, "*.tok"))
            {
                File.Delete(file);
            }
        }

        return Task.CompletedTask;
    }

    private static string RequireSafe(string key)
        => IsSafeKey(key) ? key : throw new ArgumentException("A token key must be letters, digits, '-' or '_' (it becomes a file name).", nameof(key));

    [GeneratedRegex("^[A-Za-z0-9_-]{1,128}$")]
    private static partial Regex SafeKey();
}
