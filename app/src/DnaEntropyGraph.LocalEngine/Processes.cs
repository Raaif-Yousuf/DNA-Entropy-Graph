using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace DnaEntropyGraph.LocalEngine;

/// <summary>One program to start: the file, its arguments (never a single shell string, so a path with a space or a quote is safe) and its settings.</summary>
public sealed record ProcessSpec(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string>? Environment = null);

/// <summary>A program that was started. <see cref="Exited"/> completes with its exit code once it ends, however it ends.</summary>
public interface IRunningProcess : IDisposable
{
    Task<int> Exited { get; }

    /// <summary>Ends the process, and every process it started when <paramref name="entireTree"/> is set. Safe to call after it has ended.</summary>
    void Kill(bool entireTree);
}

/// <summary>
/// The one place the app starts a child program (Hard Rule 6: the worker is only ever a child process). Behind an interface so
/// every caller is tested without a real program. <see cref="Start"/> throws <see cref="ProcessStartFailedException"/> when the program cannot be started.
/// </summary>
public interface IProcessLauncher
{
    IRunningProcess Start(ProcessSpec spec, Action<string>? onOutputLine = null);
}

public sealed class ProcessStartFailedException(string fileName, Exception? inner = null)
    : Exception("Could not start " + Path.GetFileName(fileName) + ".", inner);

/// <param name="Started">False when the program could not be started at all (<paramref name="ExitCode"/> is then -1).</param>
public sealed record ProcessOutput(bool Started, int ExitCode, string Text);

public static class ProcessLauncherExtensions
{
    /// <summary>Starts the program, waits for it to end and returns everything it printed. A cancelled wait kills the whole tree.</summary>
    public static async Task<ProcessOutput> RunToEndAsync(this IProcessLauncher launcher, ProcessSpec spec, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        var text = new StringBuilder();
        IRunningProcess process;
        try
        {
            process = launcher.Start(spec, line =>
            {
                lock (text)
                {
                    text.AppendLine(line);
                }
            });
        }
        catch (ProcessStartFailedException)
        {
            return new ProcessOutput(false, -1, string.Empty);
        }

        using (process)
        {
            using var timeoutSource = new CancellationTokenSource();
            if (timeout is { } limit)
            {
                timeoutSource.CancelAfter(limit);
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
            try
            {
                var code = await process.Exited.WaitAsync(linked.Token).ConfigureAwait(false);
                lock (text)
                {
                    return new ProcessOutput(true, code, text.ToString());
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                process.Kill(entireTree: true);
                lock (text)
                {
                    return new ProcessOutput(true, -1, text.ToString());
                }
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireTree: true);
                throw;
            }
        }
    }
}

/// <summary>The real launcher: <see cref="Process"/>, no window, output read line by line.</summary>
public sealed class SystemProcessLauncher : IProcessLauncher
{
    public IRunningProcess Start(ProcessSpec spec, Action<string>? onOutputLine = null)
    {
        var info = new ProcessStartInfo(spec.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in spec.Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        if (spec.WorkingDirectory is not null)
        {
            info.WorkingDirectory = spec.WorkingDirectory;
        }

        // The worker prints ASCII, but Python on Windows picks the console code page for pipes unless told.
        info.Environment["PYTHONIOENCODING"] = "utf-8";
        info.Environment["PYTHONUTF8"] = "1";
        foreach (var (key, value) in spec.Environment ?? new Dictionary<string, string>())
        {
            info.Environment[key] = value;
        }

        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Exited += (_, _) =>
        {
            try
            {
                // Lets the asynchronous output readers drain before the exit is reported.
                process.WaitForExit();
                exited.TrySetResult(process.ExitCode);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                exited.TrySetResult(-1);
            }
        };
        if (onOutputLine is not null)
        {
            process.OutputDataReceived += (_, e) => Forward(e.Data, onOutputLine);
            process.ErrorDataReceived += (_, e) => Forward(e.Data, onOutputLine);
        }

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException or DirectoryNotFoundException)
        {
            process.Dispose();
            throw new ProcessStartFailedException(spec.FileName, ex);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return new SystemRunning(process, exited.Task);
    }

    private static void Forward(string? line, Action<string> sink)
    {
        if (line is not null)
        {
            sink(line);
        }
    }

    private sealed class SystemRunning(Process process, Task<int> exited) : IRunningProcess
    {
        public Task<int> Exited { get; } = exited;

        public void Kill(bool entireTree)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireTree);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                // Already gone, or not ours to end any more: nothing left to do.
            }
        }

        public void Dispose() => process.Dispose();
    }
}
