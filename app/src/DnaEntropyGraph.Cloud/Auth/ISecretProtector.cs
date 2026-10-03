namespace DnaEntropyGraph.Cloud.Auth;

/// <summary>Encrypts the bytes of a token file. DPAPI on Windows; a test double elsewhere.</summary>
public interface ISecretProtector
{
    byte[] Protect(byte[] plain);

    /// <summary>Throws <see cref="System.Security.Cryptography.CryptographicException"/> for bytes this user cannot decrypt (another user, another machine, corruption).</summary>
    byte[] Unprotect(byte[] protectedBytes);
}
