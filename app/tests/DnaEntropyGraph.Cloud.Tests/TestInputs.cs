using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Contract;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>
/// Real input files and output folders for runner tests (issue #460): a run
/// now uploads real bytes and downloads real files, so every request a test
/// builds must carry inputs that exist on disk. One temp root per process,
/// removed when the process exits.
/// </summary>
internal static class TestInputs
{
    private static readonly string Root = CreateRoot();

    public static string InputContent(string name) => $"LOCUS       {name}\nORIGIN\n        1 acgtacgtac\n//\n";

    // Unique per call, never keyed by the job id alone: several test classes reuse job-1..job-5 and run in
    // parallel, and a shared path made one test's File.WriteAllText collide with another's read (finding 10).
    public static StagedInput Stage(string jobId, string fileName = "seq.gb")
    {
        var dir = Path.Combine(Root, jobId + "-" + Guid.NewGuid().ToString("N"), "input");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        File.WriteAllText(path, InputContent(Path.GetFileNameWithoutExtension(fileName)));
        return new StagedInput(path, fileName);
    }

    public static string OutputParent(string jobId)
    {
        var dir = Path.Combine(Root, jobId + "-" + Guid.NewGuid().ToString("N"), "out");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static CloudJobRequest Request(
        string jobId,
        VmSpec spec,
        IReadOnlyList<string>? zones = null,
        AfterTaskAction afterTask = AfterTaskAction.Stop,
        string? workerImage = null,
        params string[] fileNames)
    {
        var names = fileNames.Length == 0 ? ["seq.gb"] : fileNames;
        return new CloudJobRequest(
            JobId: jobId,
            Spec: spec,
            Zones: zones ?? ["us-central1-a"],
            Options: new RunOptions { ModelId = "evo2_7b", RunTarget = "Cloud", AfterTask = afterTask },
            Inputs: names.Select(n => Stage(jobId, n)).ToList(),
            OutputFolder: OutputParent(jobId),
            AfterTask: afterTask,
            WorkerImage: workerImage);
    }

    private static string CreateRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "deg-cloud-tests-" + Guid.NewGuid().ToString("N"));
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        };
        return path;
    }
}
