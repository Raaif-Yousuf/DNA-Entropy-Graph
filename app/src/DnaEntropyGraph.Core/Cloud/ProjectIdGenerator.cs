using System.Security.Cryptography;

namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// The id and name of a project the app creates for a user who has none (issue #50): <c>dna-entropy-</c> plus eight
/// random lowercase letters and digits, 20 characters in all, so it is a legal Google project id (6 to 30, a letter
/// first, no trailing hyphen). The suffix is random, not derived from the user, so two people on one Google account
/// never collide (Hard Rule 9).
/// </summary>
public static class ProjectIdGenerator
{
    public const string Prefix = "dna-entropy-";

    /// <summary>What the console shows. The app's user-facing name, not a sentence, so it is not in Resources.resw.</summary>
    public const string DisplayName = "DNA Entropy Graph";

    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
    private const int SuffixLength = 8;

    /// <param name="random">A seeded source for a reproducible id in tests; null uses the operating system's cryptographic source.</param>
    public static string Generate(Random? random = null)
    {
        Span<char> suffix = stackalloc char[SuffixLength];
        for (var i = 0; i < suffix.Length; i++)
        {
            var index = random is null ? RandomNumberGenerator.GetInt32(Alphabet.Length) : random.Next(Alphabet.Length);
            suffix[i] = Alphabet[index];
        }

        return Prefix + new string(suffix);
    }
}
