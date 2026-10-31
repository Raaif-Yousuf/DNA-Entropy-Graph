using System.Runtime.InteropServices;
using DnaEntropyGraph.Core.Runs;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests.Runs;

/// <summary>Issue #934 cold review (round 3): which unmarked folders are the user's own, however the path is spelled, and what happens when Windows cannot say.</summary>
public sealed class ShellFolderSetTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "deg-shell-tests", Guid.NewGuid().ToString("N"));

    public ShellFolderSetTests() => Directory.CreateDirectory(_base);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_base, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathNameW(string longPath, [Out] char[] shortPath, uint bufferLength);

    private static string? ShortSpelling(string existingPath)
    {
        var buffer = new char[1024];
        var length = GetShortPathNameW(existingPath, buffer, (uint)buffer.Length);
        return length == 0 || length >= buffer.Length ? null : new string(buffer, 0, (int)length);
    }

    // ---- N1: another spelling of the same folder is the same folder ----

    [Fact]
    public void The_verbatim_prefix_does_not_hide_a_protected_folder_on_either_side()
    {
        var documents = Path.Combine(_base, "Documents");
        Directory.CreateDirectory(documents);

        new ShellFolderSet([], [documents]).Protects(@"\\?\" + documents).ShouldBeTrue();
        new ShellFolderSet([], [@"\\?\" + documents]).Protects(documents).ShouldBeTrue();
        new ShellFolderSet([@"\\?\" + _base], []).Protects(Path.Combine(@"\\?\" + _base, "Misc")).ShouldBeTrue("a direct child of a protected parent, both spelled verbatim");
        new ShellFolderSet([], [documents]).Protects(@"\\?\" + Path.Combine(_base, "Other")).ShouldBeFalse("vacuity: the prefix alone does not protect a folder");
    }

    [Fact]
    public void The_verbatim_unc_prefix_means_the_same_share_as_the_plain_unc_path()
    {
        new ShellFolderSet([], [@"\\labserver\share\Documents"]).Protects(@"\\?\UNC\labserver\share\Documents").ShouldBeTrue();
        new ShellFolderSet([], [@"\\?\UNC\labserver\share\Documents"]).Protects(@"\\labserver\share\Documents").ShouldBeTrue();
        new ShellFolderSet([], [@"\\labserver\share\Documents"]).Protects(@"\\?\UNC\labserver\share\Other").ShouldBeFalse();
    }

    [Fact]
    public void An_8_3_short_name_of_a_protected_folder_is_the_same_folder_on_either_side()
    {
        var profile = Path.Combine(_base, "A Profile With A Long Name");
        var documents = Path.Combine(profile, "My Documents Folder");
        var misc = Path.Combine(profile, "Some Other Folder");
        Directory.CreateDirectory(documents);
        Directory.CreateDirectory(misc);
        var shortDocuments = ShortSpelling(documents);
        var shortMisc = ShortSpelling(misc);
        if (shortDocuments is null || shortMisc is null || string.Equals(shortDocuments, documents, StringComparison.OrdinalIgnoreCase))
        {
            Assert.Skip("Short (8.3) names are disabled on this volume.");
        }

        var set = new ShellFolderSet([profile], [documents]);
        set.Protects(shortDocuments).ShouldBeTrue("the folder spelled with short names");
        set.Protects(shortMisc).ShouldBeTrue("a direct child of the profile, spelled with short names");
        new ShellFolderSet([ShortSpelling(profile)!], [shortDocuments]).Protects(documents).ShouldBeTrue("the set itself spelled with short names");
        set.Protects(Path.Combine(_base, "Unrelated")).ShouldBeFalse("vacuity: short-name expansion protects nothing else");
    }

    [Fact]
    public void A_short_name_is_expanded_on_both_sides_before_comparing_even_where_the_volume_has_no_short_names()
    {
        // A stand-in for GetLongPathNameW: BOB~1 is "Bobby Long Name" in any path that exists (the fake treats every path as existing).
        static string? Expand(string path) => path.Replace("BOB~1", "Bobby Long Name", StringComparison.OrdinalIgnoreCase);
        var drive = Path.GetPathRoot(_base)!;
        var set = new ShellFolderSet([Path.Combine(drive, "Bobby Long Name")], [Path.Combine(drive, "Bobby Long Name", "Documents")]) { LongPathResolver = Expand };

        set.Protects(Path.Combine(drive, "BOB~1", "Documents")).ShouldBeTrue();
        set.Protects(Path.Combine(drive, "BOB~1", "Misc")).ShouldBeTrue("a direct child of the profile spelled with its short name");
        set.Protects(Path.Combine(drive, "bob~1")).ShouldBeTrue("the profile itself, any case");
        new ShellFolderSet([Path.Combine(drive, "BOB~1")], []) { LongPathResolver = Expand }.Protects(Path.Combine(drive, "Bobby Long Name", "Misc")).ShouldBeTrue("the set spelled short");
        new ShellFolderSet([Path.Combine(drive, "Bobby Long Name")], []).Protects(Path.Combine(drive, "BOB~1x", "Misc")).ShouldBeFalse("vacuity: without a resolver that matches, nothing is expanded");
    }

    // ---- N2: fail closed ----

    [Fact]
    public void When_the_profile_cannot_be_resolved_every_folder_is_protected()
    {
        var set = ShellFolderSet.Build(profile: "", desktop: "", documents: "", pictures: "", music: "", videos: "", downloads: null);

        set.Protects(Path.Combine(_base, "anything")).ShouldBeTrue();
        set.Protects(@"Z:\whatever\else").ShouldBeTrue();
    }

    [Fact]
    public void A_shell_folder_that_Windows_does_not_report_falls_back_to_its_usual_place_in_the_profile()
    {
        var profile = Path.Combine(_base, "bob");
        var set = ShellFolderSet.Build(profile, desktop: "", documents: " ", pictures: null, music: "", videos: "", downloads: null);

        foreach (var name in new[] { "Desktop", "Documents", "Pictures", "Music", "Videos", "Downloads" })
        {
            set.Protects(Path.Combine(profile, name)).ShouldBeTrue(name);
        }

        set.Protects(Path.Combine(profile, "Downloads", "run-1")).ShouldBeFalse("ordinary run folders live directly inside Downloads");
    }

    [Fact]
    public void A_redirected_Downloads_folder_is_protected_and_the_usual_one_is_too()
    {
        var profile = Path.Combine(_base, "bob");
        var redirected = Path.Combine(_base, "Cloud", "Downloads");
        var set = ShellFolderSet.Build(profile, "", "", "", "", "", redirected);

        set.Protects(redirected).ShouldBeTrue();
        set.Protects(Path.Combine(profile, "Downloads")).ShouldBeTrue();
        set.Protects(Path.Combine(redirected, "run-1")).ShouldBeFalse();
    }

    // ---- N3: the shell folders themselves and the profile root's children, not the children of Documents and the like ----

    [Fact]
    public void The_profile_and_its_direct_children_are_protected_but_not_the_children_of_Documents()
    {
        var profile = Path.Combine(_base, "bob");
        var set = ShellFolderSet.Build(profile, "", "", "", "", "", null);

        set.Protects(profile).ShouldBeTrue();
        set.Protects(Path.Combine(profile, "Misc")).ShouldBeTrue();
        set.Protects(Path.Combine(profile, "Documents")).ShouldBeTrue();
        set.Protects(Path.Combine(profile, "Documents", "thesis")).ShouldBeFalse("older runs live here when the user chose Documents; they get the one confirmation");
        set.Protects(Path.Combine(profile, "Desktop", "lab")).ShouldBeFalse();
        set.Protects(Path.Combine(profile, "Misc", "deeper")).ShouldBeFalse();
    }
}
