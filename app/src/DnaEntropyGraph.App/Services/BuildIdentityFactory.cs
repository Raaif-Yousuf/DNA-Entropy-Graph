using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using DnaEntropyGraph.Presentation.Services;

namespace DnaEntropyGraph.App.Services;

/// <summary>
/// Issue #263: builds the <see cref="BuildIdentity"/> from the running exe. "Signed" means the exe carries an embedded Authenticode
/// signature, read at run time rather than set by a build flag: release.yml signs the exe and fails unless <c>signtool verify /pa</c>
/// accepts it, so a signature on the file is the fact a banner should follow, and a flag could say "signed" for an exe where the signing
/// step silently no-opped (the known Azure Trusted Signing footgun). It checks presence, not the chain: a revoked or expired certificate
/// still reads as signed, and the verify job is what proves validity. Chain validation (WinVerifyTrust) was judged not cheap: it is a P/Invoke
/// with a revocation lookup that can touch the network on every start-up, for a banner whose false-positive (a signed-looking but invalid
/// exe) needs someone to have tampered with the installed file. Not done; presence is the contract.
/// </summary>
public static class BuildIdentityFactory
{
    public static BuildIdentity Create()
    {
        var informational = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return new BuildIdentity(BuildInfo.Parse(informational), HasAuthenticodeSignature(ResolveOwnBinary(Assembly.GetExecutingAssembly().Location, Environment.ProcessPath)));
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
        catch (Exception ex)
        {
            // No signature (the usual case for a dev build), an unreadable file, a locked one: all "not a signed release". This runs at start-up,
            // so nothing may escape; only the class is traced (a message can carry a path).
            System.Diagnostics.Trace.TraceWarning($"signature check failed: {ex.GetType().Name}");
            return false;
        }
    }

    /// <summary>
    /// The file whose signature says "this is a release": the app's own binary, never the host that launched it. <c>dotnet App.dll</c> makes
    /// <see cref="Environment.ProcessPath"/> Microsoft's signed dotnet.exe, which would read as a signed release. So it is the apphost
    /// <c>.exe</c> beside the assembly when there is one (release.yml signs that), else the assembly itself; only a single-file publish, whose
    /// assembly has no location, falls back to the process path (there the process is the app).
    /// </summary>
    public static string? ResolveOwnBinary(string? assemblyLocation, string? processPath)
    {
        if (string.IsNullOrEmpty(assemblyLocation))
        {
            return processPath;
        }

        var apphost = Path.ChangeExtension(assemblyLocation, ".exe");
        return File.Exists(apphost) ? apphost : assemblyLocation;
    }

    private static X509Certificate? ReadSigner(string path)
#pragma warning disable SYSLIB0057 // CreateFromSignedFile is the only API that reads a file's embedded signer.
        => X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
}
