namespace DnaEntropyGraph.Presentation.Services;

/// <summary>
/// Issue #179 review: the channel rule, checked on the version itself and not only on GitHub's <c>prerelease</c> flag (a release
/// published without the flag but versioned <c>1.3.0-rc.1</c> must not reach a Stable user). Stable takes only a version without a
/// semver pre-release tag; Beta takes everything.
/// </summary>
public static class UpdateChannelPolicy
{
    public static bool Allows(UpdateChannel channel, string version)
    {
        if (channel == UpdateChannel.Beta)
        {
            return true;
        }

        // Semver: MAJOR.MINOR.PATCH[-prerelease][+build]. Build metadata may itself contain '-', so cut it off first.
        var core = version.Split('+', 2)[0];
        return !core.Contains('-', StringComparison.Ordinal);
    }
}
