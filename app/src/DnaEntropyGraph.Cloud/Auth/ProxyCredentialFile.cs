using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DnaEntropyGraph.Core.Net;

namespace DnaEntropyGraph.Cloud.Auth;

/// <summary>
/// The manual proxy sign-in (issue #239): one file under the app's auth folder, encrypted by <see cref="ISecretProtector"/> (Windows DPAPI,
/// this user only), like the token files. Never settings.json or SQLite (Hard Rule 12). The file is written with a temp file and a move,
/// so a crash cannot leave half a credential. Neither the name nor the password is ever logged.
/// </summary>
public sealed class ProxyCredentialFile(string path, ISecretProtector protector) : IProxyCredentialStore
{
    private sealed record Payload(string UserName, string Password);

    public NetworkCredential? Load()
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var plain = protector.Unprotect(File.ReadAllBytes(path));
            var payload = JsonSerializer.Deserialize<Payload>(Encoding.UTF8.GetString(plain));
            return payload is { UserName.Length: > 0 } ? new NetworkCredential(payload.UserName, payload.Password) : null;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException or UnauthorizedAccessException or PlatformNotSupportedException or ArgumentException)
        {
            throw new ProxyCredentialUnreadableException("the saved proxy sign-in could not be read", ex);
        }
    }

    public void Save(NetworkCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var bytes = protector.Protect(JsonSerializer.SerializeToUtf8Bytes(new Payload(credential.UserName, credential.Password)));
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            throw new ProxyCredentialUnreadableException("the proxy sign-in could not be saved", ex);
        }
    }

    public void Clear()
    {
        try
        {
            File.Delete(path);
        }
        catch (DirectoryNotFoundException)
        {
            // No folder yet means no file: nothing to remove.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ProxyCredentialUnreadableException("the saved proxy sign-in could not be removed", ex);
        }
    }
}
