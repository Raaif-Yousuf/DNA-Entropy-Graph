using System.Security.Cryptography;
using System.Text;

namespace DnaEntropyGraph.Cloud.Auth;

/// <summary>
/// Windows DPAPI, <see cref="DataProtectionScope.CurrentUser"/>: only this Windows user on this PC can read the file
/// (docs/threat_model.md). The entropy is a fixed app string, so another program running as the same user cannot
/// read the file by calling DPAPI with no entropy; it is not a secret and is not a defence against malware that
/// runs as the user and knows it.
/// </summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DNAEntropyGraph.auth.v1");

    public byte[] Protect(byte[] plain)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("DPAPI is Windows only.");
        }

        return ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
    }

    public byte[] Unprotect(byte[] protectedBytes)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("DPAPI is Windows only.");
        }

        return ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
    }
}
