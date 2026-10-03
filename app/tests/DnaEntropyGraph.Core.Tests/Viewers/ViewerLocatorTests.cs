using DnaEntropyGraph.Core.Viewers;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests.Viewers;

public sealed class ViewerLocatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "deg-viewer-locator", Guid.NewGuid().ToString("N"));

    public ViewerLocatorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Igv_is_found_in_its_versioned_install_folder_and_the_newest_version_wins()
    {
        Make(@"IGV-2.16.2\igv.exe");
        var newest = Make(@"IGV-2.19.7\igv.exe");

        Locator().Find(ExternalViewer.Igv).ShouldBe(newest);
    }

    [Fact]
    public void Igv_falls_back_to_the_bat_file_when_there_is_no_exe()
    {
        var bat = Make(@"IGV-2.19.7\igv.bat");

        Locator().Find(ExternalViewer.Igv).ShouldBe(bat);
    }

    [Fact]
    public void Geneious_is_found_in_its_install_folder()
    {
        var exe = Make(@"Geneious Prime\Geneious Prime.exe");

        Locator().Find(ExternalViewer.Geneious).ShouldBe(exe);
    }

    [Fact]
    public void Nothing_installed_is_null_and_a_folder_for_the_other_viewer_does_not_count()
    {
        Make(@"Geneious Prime\Geneious Prime.exe");

        Locator().Find(ExternalViewer.Igv).ShouldBeNull();
    }

    [Fact]
    public void A_root_that_does_not_exist_is_skipped()
    {
        new ViewerLocator(() => [Path.Combine(_root, "nope")]).Find(ExternalViewer.Igv).ShouldBeNull();
    }

    private string Make(string relative)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }

    private ViewerLocator Locator() => new(() => [_root]);
}
