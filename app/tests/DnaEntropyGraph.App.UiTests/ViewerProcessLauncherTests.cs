using System.Diagnostics;
using DnaEntropyGraph.App.Services;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.App.UiTests;

/// <summary>Issue #586: the launcher's own backstop against cmd.exe metacharacters, proved without a process ever being created.</summary>
public sealed class ViewerProcessLauncherTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "deg-viewer-launcher", Guid.NewGuid().ToString("N"));

    public ViewerProcessLauncherTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void A_batch_file_with_an_ampersand_argument_is_refused_before_any_process_is_started()
    {
        var bat = Path.Combine(_dir, "igv.bat");
        File.WriteAllText(bat, "rem");
        var started = new List<ProcessStartInfo>();
        var launcher = new ViewerProcessLauncher(started.Add);

        launcher.Launch(bat, [@"C:\Lab\R&D\a.fasta"]).ShouldBeFalse();
        started.ShouldBeEmpty();

        // The same file with a plain argument does start: the refusal above was the guard, not a missing file.
        launcher.Launch(bat, [@"C:\Lab\RD\a.fasta"]).ShouldBeTrue();
        started.Count.ShouldBe(1);
    }

    [Fact]
    public void An_exe_takes_the_same_argument()
    {
        var exe = Path.Combine(_dir, "igv.exe");
        File.WriteAllText(exe, "x");
        var started = new List<ProcessStartInfo>();

        new ViewerProcessLauncher(started.Add).Launch(exe, [@"C:\Lab\R&D\a.fasta"]).ShouldBeTrue();

        started.Single().ArgumentList.ShouldBe([@"C:\Lab\R&D\a.fasta"]);
    }
}
