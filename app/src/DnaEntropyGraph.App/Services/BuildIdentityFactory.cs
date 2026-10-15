using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using DnaEntropyGraph.Presentation.Services;

namespace DnaEntropyGraph.App.Services;

/// <summary>
/// Issue #263: builds the <see cref="BuildIdentity"/> from the running exe. "Signed" means the exe carries an embedded Authenticode
/// signature, read at run time rather than set by a build flag: release.yml signs the exe and fails unless <c>signtool verify /pa</c>
/// accepts it, so a signature on the file is the fact a banner should follow, and a flag could say "signed" for an exe where the signing
/// step silently no-opped (the known Azure Trusted Signing footgun). It checks presence, not the chain: a revoked or expired certificate
/// still reads as signed, and the verify job is what proves validity.
/// </summary>
public static class BuildIdentityFactory
{
    public static BuildIdentity Create()
    {
        var informational = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return new BuildIdentity(BuildInfo.Parse(informational), HasAuthenticodeSignature(Environment.ProcessPath));
    }

    /// <summary>True when the file at <paramref name="path"/> has an embedded Authenticode signature; false for a missing path, an unsigned file or one that cannot be read.</summary>
    public static bool HasAuthenticodeSignature(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            using var certificate = ReadSigner(path);
            return certificate is not null;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // No signature (the usual case for a dev build), or an unreadable one: either way, not a signed release.
            return false;
        }
    }

    private static X509Certificate? ReadSigner(string path)
#pragma warning disable SYSLIB0057 // CreateFromSignedFile is the only API that reads a file's embedded signer.
        => X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
}
