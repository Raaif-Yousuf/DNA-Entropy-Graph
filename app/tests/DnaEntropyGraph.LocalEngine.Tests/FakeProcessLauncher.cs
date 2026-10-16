namespace DnaEntropyGraph.LocalEngine.Tests;

/// <summary>A launcher that starts nothing: a script decides what each started program prints and when it ends.</summary>
internal sealed class FakeProcessLauncher : IProcessLauncher
{
    private readonly Func<ProcessSpec, FakeProcess?> _script;

    public FakeProcessLauncher(Func<ProcessSpec, FakeProcess?> script) => _script = script;

    public List<ProcessSpec> Started { get; } = [];

    public List<FakeProcess> Processes { get; } = [];

    public IRunningProcess Start(ProcessSpec spec, Action<string>? onOutputLine = null)
    {
        Started.Add(spec);
        var process = _script(spec) ?? throw new ProcessStartFailedException(spec.FileName);
        process.Sink = onOutputLine;
        process.FlushPending();
        Processes.Add(process);
        return process;
    }

    /// <summary>Starts nothing that is not listed; every listed program ends at once with its code and text.</summary>
    public static FakeProcessLauncher Answering(params (string FileNameContains, int ExitCode, string Output)[] answers)
        => new(spec =>
        {
            foreach (var (needle, code, output) in answers)
            {
                if (spec.FileName.Contains(needle, StringComparison.OrdinalIgnoreCase) || spec.Arguments.Any(a => a.Contains(needle, StringComparison.OrdinalIgnoreCase)))
                {
                    var process = new FakeProcess();
                    foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        process.Print(line.TrimEnd('\r'));
                    }

                    process.End(code);
                    return process;
                }
            }

            return null;
        });
}

internal sealed class FakeProcess : IRunningProcess
{
    private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<string> _pending = [];

    public Action<string>? Sink { get; set; }

    public Task<int> Exited => _exit.Task;

    public bool Killed { get; private set; }

    public bool KilledTree { get; private set; }

    public void Print(string line)
    {
        if (Sink is null)
        {
            _pending.Add(line);
        }
        else
        {
            Sink(line);
        }
    }

    public void FlushPending()
    {
        foreach (var line in _pending)
        {
            Sink?.Invoke(line);
        }

        _pending.Clear();
    }

    public void End(int exitCode) => _exit.TrySetResult(exitCode);

    public void Kill(bool entireTree)
    {
        Killed = true;
        KilledTree = entireTree;
        _exit.TrySetResult(-1);
    }

    public void Dispose()
    {
    }
}
