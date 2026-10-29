using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Contract;
using DnaEntropyGraph.Core.LocalEngine;
using DnaEntropyGraph.Core.Runs;

namespace DnaEntropyGraph.LocalEngine;

/// <summary>Everything one This PC run needs. <see cref="Input"/> is the app's own staged copy (Hard Rule 14), never the user's file. <paramref name="CudaDevice"/> is the card the worker must use (its <c>GPU-...</c> uuid): the one the readiness check judged, not whichever CUDA happens to number first.</summary>
public sealed record LocalJobRequest(
    string JobId,
    RunOptions Options,
    StagedInput Input,
    string InstallationId,
    string AppVersion,
    string OutputFolder,
    string? CudaDevice = null);

/// <summary>How a This PC run ended. <see cref="ErrorCode"/> is a <see cref="RunErrorCodes"/> code when it did not complete.</summary>
public sealed record LocalJobResult(JobPhase FinalPhase, string? ErrorCode = null, string? ErrorDetail = null);

/// <summary>
/// Issue #174: runs the worker for a This PC run as a child process (Hard Rule 6), over the same contract as a cloud run.
/// The job folder <c>runs\&lt;jobId&gt;\</c> is the bucket prefix: <c>manifest.json</c> goes in, the worker (started with
/// <c>--store localdir</c>) writes <c>status.json</c>, <c>progress.jsonl</c>, <c>output\</c> and <c>result.json</c>, and the files
/// <c>result.json</c> lists are copied, verified, into a fresh folder in the output folder (Hard Rule 14), the same as a download.
/// <para>
/// DECISION (agent-made, reversible): <c>status.json</c> is polled every <see cref="PollInterval"/> (500 ms) and not also watched with
/// a <c>FileSystemWatcher</c> as the issue lists. The worker replaces the file atomically on a timer, the read is a few hundred
/// bytes from local disk, and a watcher on a folder that is being renamed into misses events and would need the poll anyway.
/// </para>
/// A user cancel is cooperative first (<c>control/cancel</c>, which the worker checks between windows), then the whole process
/// tree is killed after <see cref="CancelGrace"/>. The runner records a failure itself and never throws for one; it throws
/// <see cref="OperationCanceledException"/> only for a cancel the caller asked for, after the worker has stopped.
/// </summary>
public sealed class LocalJobRunner
{
    private readonly IProcessLauncher _launcher;
    private readonly IRunRepository _runs;
    private readonly LocalEngineLayout _layout;
    private readonly string _runsDirectory;
    private readonly Action<string, JobPhase>? _onPhaseChanged;
    private readonly IDiagnosticsLog _log;

    public LocalJobRunner(
        IProcessLauncher launcher,
        IRunRepository runs,
        LocalEngineLayout layout,
        string runsDirectory,
        Action<string, JobPhase>? onPhaseChanged = null,
        IDiagnosticsLog? log = null)
    {
        _log = log ?? NullDiagnosticsLog.Instance;
        _launcher = launcher;
        _runs = runs;
        _layout = layout;
        _runsDirectory = runsDirectory;
        _onPhaseChanged = onPhaseChanged;
    }

    /// <summary>How often <c>status.json</c> is read while the worker runs.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>The clock every deadline below runs on (virtual in tests, #525).</summary>
    public TimeProvider Clock { get; init; } = TimeProvider.System;

    /// <summary>
    /// How long after the process starts its first heartbeat may take. THEORY (unverified): ten minutes covers a cold start of Python and torch on a PC
    /// with a virus scanner. This is the app's own deadline, not the worker's.
    /// </summary>
    public TimeSpan FirstHeartbeatDeadline { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>How long a heartbeat may stay unchanged (the worker writes one every 30 s, docs/job_contract.md section 5) before the worker is called frozen: 20 beats, the cloud's own rule.</summary>
    public TimeSpan StaleHeartbeat { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Added to the run's own maximum duration before the app kills the worker: the worker's cap (<c>limits.maxRunSeconds</c>) gets to fire first and say why, and
    /// this is the backstop for a worker that does not honour it (the app must not rely on the worker side).
    /// </summary>
    public TimeSpan MaxRunMargin { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How long a cancelled worker has to stop by itself before its process tree is killed (issue #174: 30 s).</summary>
    public TimeSpan CancelGrace { get; init; } = TimeSpan.FromSeconds(30);

    public async Task<LocalJobResult> RunAsync(LocalJobRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var jobDir = Path.Combine(_runsDirectory, request.JobId);
        if (!RunOutputRoot.IsStrictlyInside(jobDir, _runsDirectory))
        {
            return await FailAsync(request.JobId, RunErrorCodes.ManifestInvalid, "The job id is not a plain name.").ConfigureAwait(false);
        }

        string manifestPath;
        try
        {
            manifestPath = await PrepareJobFolderAsync(request, jobDir, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return await FailAsync(request.JobId, RunErrorCodes.InputMissing, ex.GetType().Name).ConfigureAwait(false);
        }

        await SetPhaseAsync(request.JobId, JobPhase.Preparing).ConfigureAwait(false);

        // Made, and proven writable, before the worker starts: an unusable output folder is a quick failure, not a lost run.
        string runFolder;
        try
        {
            runFolder = RunOutputFolders.CreateUnique(
                request.OutputFolder,
                RunOutputFolders.RunFolderName(request.Options, request.Input, Clock.GetLocalNow().DateTime));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return await FailAsync(request.JobId, RunErrorCodes.OutputFolderUnusable, "The output folder could not be created: " + ex.GetType().Name + ".").ConfigureAwait(false);
        }

        await UpdateRowAsync(request.JobId, row => row with { OutputDir = runFolder }).ConfigureAwait(false);

        var spec = new ProcessSpec(
            _layout.PythonExe,
            ["-m", "dna_entropy.worker.cli", "run", "--manifest", manifestPath, "--store", "localdir", "--root", jobDir],
            WorkingDirectory: jobDir,
            Environment: EnvironmentFor(request),
            ClearEnvironment: _layout.ClearedVariables);

        IRunningProcess process;
        try
        {
            process = _launcher.Start(spec);
        }
        catch (ProcessStartFailedException)
        {
            await RemoveEmptyFolderAsync(request.JobId, runFolder).ConfigureAwait(false);
            return await FailAsync(request.JobId, RunErrorCodes.LocalEngineNotReady, "The engine's python could not be started.").ConfigureAwait(false);
        }

        using (process)
        {
            await SetPhaseAsync(request.JobId, JobPhase.Running).ConfigureAwait(false);
            WatchOutcome watched;
            try
            {
                watched = await WatchAsync(request, jobDir, process, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Whatever went wrong on the app side, the worker must not be left running with nobody watching it.
                process.Kill(entireTree: true);
                throw;
            }
            catch (OperationCanceledException)
            {
                // The worker is stopped before the cancel is acknowledged; whatever it had finished is kept (docs/job_contract.md section 6).
                await StopWorkerAsync(jobDir, process).ConfigureAwait(false);
                await CopyPartialAsync(request.JobId, jobDir, runFolder).ConfigureAwait(false);
                throw;
            }

            if (watched.StopCode is { } stopCode)
            {
                // Killed by the app's own deadline: keep whatever the worker had finished, then record the named cause.
                await CopyPartialAsync(request.JobId, jobDir, runFolder).ConfigureAwait(false);
                return await FailAsync(request.JobId, stopCode, watched.StopDetail ?? string.Empty).ConfigureAwait(false);
            }

            return await FinishAsync(request.JobId, jobDir, runFolder, watched.ExitCode).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Ends a run that the caller could not finish (an exception nobody expected, or a start that was refused) in <see cref="JobPhase.Failed"/> with a code.
    /// A run already at a terminal phase never moves. Only the code and the exception class are kept: the exception's text can hold a path.
    /// </summary>
    public async Task RecordFailureAsync(string jobId, string code, string detail)
        => await FailAsync(jobId, code, detail).ConfigureAwait(false);

    /// <summary>Records the end of a user cancel: <see cref="JobPhase.Cancelling"/> then <see cref="JobPhase.Cancelled"/>. Call after <see cref="RunAsync"/> has stopped.</summary>
    public async Task RecordCancelledAsync(string jobId)
    {
        await SetPhaseAsync(jobId, JobPhase.Cancelling).ConfigureAwait(false);
        await SetPhaseAsync(jobId, JobPhase.Cancelled).ConfigureAwait(false);
    }

    /// <summary>
    /// Run once at launch (issue #734), before any run is started: a This PC row a killed or closed app left non-terminal has no driver, and the
    /// worker died with the app (<see cref="WindowsKillOnCloseJob"/>). With a finished <c>result.json</c> and an output folder the run is finished from it
    /// (the files are copied and verified as for any run); otherwise it ends Failed as <see cref="RunErrorCodes.LocalRunInterrupted"/>, and a Cancelling
    /// row ends Cancelled. Cloud and finished rows are not touched. Returns how many rows were judged. One row's failure never stops the others.
    /// </summary>
    public async Task<int> ReconcileInterruptedAsync(CancellationToken cancellationToken)
    {
        var rows = (await _runs.GetAllAsync(cancellationToken).ConfigureAwait(false))
            .Where(r => RunTargets.IsLocalRecord(r.Target) && !JobStateMachine.IsTerminal(r.Phase))
            .ToList();
        var judged = 0;
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await ReconcileOneAsync(row).ConfigureAwait(false);
                judged++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                _log.Warning("local-reconcile", row.JobId, ex.GetType().Name);
            }
        }

        return judged;
    }

    private async Task ReconcileOneAsync(RunRecord row)
    {
        if (row.Phase == JobPhase.Cancelling)
        {
            await SetPhaseAsync(row.JobId, JobPhase.Cancelled).ConfigureAwait(false);
            return;
        }

        var jobDir = Path.Combine(_runsDirectory, row.JobId);
        var finished = RunOutputRoot.IsStrictlyInside(jobDir, _runsDirectory)
            && File.Exists(Path.Combine(jobDir, "result.json"))
            && !string.IsNullOrEmpty(row.OutputDir)
            && Directory.Exists(row.OutputDir);
        if (finished)
        {
            await FinishAsync(row.JobId, jobDir, row.OutputDir!, exitCode: 0).ConfigureAwait(false);
            return;
        }

        await FailAsync(row.JobId, RunErrorCodes.LocalRunInterrupted, "The app was closed or stopped while the run was in progress.").ConfigureAwait(false);
    }
    private async Task<string> PrepareJobFolderAsync(LocalJobRequest request, string jobDir, CancellationToken cancellationToken)
    {
        var inputDir = Path.Combine(jobDir, "input");
        Directory.CreateDirectory(inputDir);
        // The app's own copy of the input is normally ALREADY here: runs\<jobId>\input\<file> is where the input store keeps it
        // (Hard Rule 14), and it is the job folder's own input\ path. Copying a file onto itself is an error, so only a copy from
        // somewhere else is made.
        var destination = Path.Combine(inputDir, request.Input.FileName);
        if (!string.Equals(Path.GetFullPath(request.Input.LocalPath), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(request.Input.LocalPath, destination, overwrite: true);
        }


        var manifest = WorkerManifestBuilder.Build(
            request.Options,
            request.JobId,
            request.InstallationId,
            request.AppVersion,
            workerImage: null,
            bucket: string.Empty,
            [request.Input],
            DateTimeOffset.UtcNow,
            localRoot: jobDir);

        var manifestPath = Path.Combine(jobDir, "manifest.json");
        await File.WriteAllTextAsync(manifestPath, manifest, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        await UpdateRowAsync(request.JobId, row => row with { ManifestJson = manifest }).ConfigureAwait(false);
        return manifestPath;
    }

    private IReadOnlyDictionary<string, string>? EnvironmentFor(LocalJobRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.CudaDevice))
        {
            return _layout.Environment;
        }

        var merged = new Dictionary<string, string>(_layout.Environment ?? new Dictionary<string, string>()) { ["CUDA_VISIBLE_DEVICES"] = request.CudaDevice };
        return merged;
    }

    /// <summary>How the wait ended: the worker's exit code, or the app's own deadline that killed it (<see cref="StopCode"/>).</summary>
    private sealed record WatchOutcome(int ExitCode, string? StopCode = null, string? StopDetail = null);

    /// <summary>
    /// Waits for the worker to end, reading its heartbeat while it runs, on <see cref="Clock"/>. Three deadlines end a run that would otherwise hang: no first
    /// heartbeat within <see cref="FirstHeartbeatDeadline"/>, a heartbeat unchanged for <see cref="StaleHeartbeat"/>, and the run's own maximum plus
    /// <see cref="MaxRunMargin"/>. Each kills the whole process tree. Throws <see cref="OperationCanceledException"/> on a cancel.
    /// </summary>
    private async Task<WatchOutcome> WatchAsync(LocalJobRequest request, string jobDir, IRunningProcess process, CancellationToken cancellationToken)
    {
        var started = Clock.GetUtcNow();
        var lastChange = started;
        var sawHeartbeat = false;
        long lastSeq = -1;
        var configured = request.Options.MaxRunDurationMinutes > 0 ? request.Options.MaxRunDurationMinutes : 240;
        var maxRun = TimeSpan.FromMinutes(configured) + MaxRunMargin;
        while (true)
        {
            var ended = await Task.WhenAny(process.Exited, Task.Delay(PollInterval, Clock, cancellationToken)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var seq = await ReadHeartbeatAsync(request.JobId, jobDir, lastSeq).ConfigureAwait(false);
            if (seq != lastSeq)
            {
                lastSeq = seq;
                sawHeartbeat = true;
                lastChange = Clock.GetUtcNow();
            }

            if (ended == process.Exited)
            {
                return new WatchOutcome(await process.Exited.ConfigureAwait(false));
            }

            var now = Clock.GetUtcNow();
            if (now - started >= maxRun)
            {
                return await KillAsync(process, RunErrorCodes.LocalWorkerTimedOut, "The run passed its maximum duration of " + configured.ToString(CultureInfo.InvariantCulture) + " minutes.").ConfigureAwait(false);
            }

            if (!sawHeartbeat && now - started >= FirstHeartbeatDeadline)
            {
                return await KillAsync(process, RunErrorCodes.LocalWorkerNoHeartbeat, "The worker wrote no heartbeat within " + FirstHeartbeatDeadline.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + " seconds of starting.").ConfigureAwait(false);
            }

            if (sawHeartbeat && now - lastChange >= StaleHeartbeat)
            {
                return await KillAsync(process, RunErrorCodes.LocalWorkerStalled, "The worker's heartbeat did not change for " + StaleHeartbeat.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + " seconds.").ConfigureAwait(false);
            }
        }
    }

    private static async Task<WatchOutcome> KillAsync(IRunningProcess process, string code, string detail)
    {
        process.Kill(entireTree: true);
        await Task.WhenAny(process.Exited, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
        return new WatchOutcome(-1, code, detail);
    }

    /// <summary>Reads the heartbeat into the row when it changed; returns the sequence last seen.</summary>
    private async Task<long> ReadHeartbeatAsync(string jobId, string jobDir, long lastSeq)
    {
        var status = TryReadStatus(Path.Combine(jobDir, "status.json"));
        if (status?.HeartbeatSeq is not { } seq || seq == lastSeq)
        {
            return lastSeq;
        }

        await UpdateRowAsync(
            jobId,
            row => row with
            {
                LastProgressSeq = (int)Math.Clamp(seq, 0, int.MaxValue),
                LastHeartbeatAt = Clock.GetUtcNow(),
                WorkerVersion = string.IsNullOrEmpty(status.WorkerVersion) ? row.WorkerVersion : status.WorkerVersion,
            }).ConfigureAwait(false);
        return seq;
    }
    private static WorkerStatusSnapshot? TryReadStatus(string path)
    {
        try
        {
            return File.Exists(path) ? WorkerStatusReader.TryParseStatus(File.ReadAllText(path, Encoding.UTF8)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null; // The worker is replacing the file this instant; the next poll reads it.
        }
    }

    /// <summary>Asks the worker to stop (the file's presence is the whole signal), and kills its process tree if it has not ended within <see cref="CancelGrace"/>.</summary>
    private async Task StopWorkerAsync(string jobDir, IRunningProcess process)
    {
        try
        {
            Directory.CreateDirectory(Path.Combine(jobDir, "control"));
            await File.WriteAllBytesAsync(Path.Combine(jobDir, "control", "cancel"), []).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cannot ask nicely; the kill below still ends it.
        }

        var finished = await Task.WhenAny(process.Exited, Task.Delay(CancelGrace, Clock)).ConfigureAwait(false);
        if (finished != process.Exited)
        {
            process.Kill(entireTree: true);
            await Task.WhenAny(process.Exited, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
        }
    }

    private async Task<LocalJobResult> FinishAsync(string jobId, string jobDir, string runFolder, int exitCode)
    {
        var resultPath = Path.Combine(jobDir, "result.json");
        WorkerResultDocument? result = null;
        try
        {
            if (File.Exists(resultPath))
            {
                result = WorkerResultReader.Parse(await File.ReadAllTextAsync(resultPath, Encoding.UTF8).ConfigureAwait(false));
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            await RemoveEmptyFolderAsync(jobId, runFolder).ConfigureAwait(false);
            return await FailAsync(jobId, RunErrorCodes.WorkerFailed, "result.json could not be read: " + ex.GetType().Name + ".").ConfigureAwait(false);
        }

        if (result is null)
        {
            // WORKER_CRASH (issue #174): the process ended and wrote no result. The exit code is the only detail kept; the worker's console
            // text can carry a path or a record name.
            await RemoveEmptyFolderAsync(jobId, runFolder).ConfigureAwait(false);
            return await FailAsync(jobId, RunErrorCodes.LocalWorkerCrashed, "The worker ended with exit code " + exitCode.ToString(CultureInfo.InvariantCulture) + " and no result.json.").ConfigureAwait(false);
        }

        var doneInputs = result.Inputs.Count(i => i.Status == "done" && i.Files.Count > 0);
        if (result.Status != "done" || doneInputs == 0)
        {
            var keptPartial = false;
            if (result.Inputs.Any(i => i.Files.Count > 0))
            {
                keptPartial = await TryCopyAsync(jobDir, runFolder, result).ConfigureAwait(false) is null;
            }

            if (!keptPartial)
            {
                await RemoveEmptyFolderAsync(jobId, runFolder).ConfigureAwait(false);
            }

            var workerCode = result.ErrorCode ?? result.Inputs.FirstOrDefault(i => i.Status != "done")?.ErrorCode;
            return await FailAsync(
                jobId,
                RunErrorCodes.ForWorkerStatusCode(workerCode) ?? RunErrorCodes.WorkerFailed,
                $"The worker reported status '{result.Status}' ({workerCode ?? "unknown"})." + (keptPartial ? " Partial files were kept in the output folder." : string.Empty)).ConfigureAwait(false);
        }

        await SetPhaseAsync(jobId, JobPhase.Finalizing).ConfigureAwait(false);
        await SetPhaseAsync(jobId, JobPhase.Downloading).ConfigureAwait(false);
        var copyFailure = await TryCopyAsync(jobDir, runFolder, result).ConfigureAwait(false);
        if (copyFailure is not null)
        {
            return await FailAsync(jobId, copyFailure.Value.Code, copyFailure.Value.Detail).ConfigureAwait(false);
        }

        var finalPhase = doneInputs == result.Inputs.Count ? JobPhase.Completed : JobPhase.PartiallyCompleted;
        // DECISION (agent-made, reversible): result.json is the worker's verdict and every file is verified against it, so a nonzero exit AFTER a finished
        // result.json (a crash while CUDA shuts down is the usual cause) does not throw good results away. The row carries a note and the exit code is logged.
        if (exitCode != 0)
        {
            _log.Warning("local-run", jobId, "worker exit code " + exitCode.ToString(CultureInfo.InvariantCulture) + " after a finished result.json");
            await SetPhaseAsync(jobId, finalPhase, RunErrorCodes.LocalWorkerExitedNonzero, "The worker exited with code " + exitCode.ToString(CultureInfo.InvariantCulture) + " after writing a finished result.json.").ConfigureAwait(false);
        }
        else
        {
            await SetPhaseAsync(jobId, finalPhase).ConfigureAwait(false);
        }

        return new LocalJobResult(finalPhase);
    }

    /// <summary>Copies what a cancelled worker finished, if it got as far as a <c>result.json</c>; a failure here is never worth reporting over the cancel.</summary>
    private async Task CopyPartialAsync(string jobId, string jobDir, string runFolder)
    {
        try
        {
            var resultPath = Path.Combine(jobDir, "result.json");
            if (File.Exists(resultPath))
            {
                var result = WorkerResultReader.Parse(await File.ReadAllTextAsync(resultPath, Encoding.UTF8).ConfigureAwait(false));
                await TryCopyAsync(jobDir, runFolder, result).ConfigureAwait(false);
            }

            // Also clears the row's OutputDir when nothing was kept: a row that names a folder that is gone is a dead link in History.
            await RemoveEmptyFolderAsync(jobId, runFolder).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            // The cancel stands; leftovers stay in the job folder.
        }
    }

    /// <summary>Copies every listed file into <paramref name="runFolder"/> and verifies it against <c>result.json</c>. Null on success, else the failure.</summary>
    private static async Task<(string Code, string Detail)?> TryCopyAsync(string jobDir, string runFolder, WorkerResultDocument result)
    {
        var runFolderFull = Path.GetFullPath(runFolder);
        var jobFull = Path.GetFullPath(jobDir);
        foreach (var input in result.Inputs.Where(i => i.Files.Count > 0))
        {
            for (var n = 0; n < input.Files.Count; n++)
            {
                var file = input.Files[n];
                var label = $"Result file {n + 1} of input {input.Id}";
                var relative = SafeRelativeOutputPath(file.Path);
                if (relative is null)
                {
                    return (RunErrorCodes.WorkerFailed, label + " has a path that is not a plain output path.");
                }

                var source = Path.GetFullPath(Path.Combine(jobFull, file.Path.Replace('/', Path.DirectorySeparatorChar)));
                var destination = Path.GetFullPath(Path.Combine(runFolderFull, relative));
                if (!destination.StartsWith(runFolderFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || !source.StartsWith(jobFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    return (RunErrorCodes.WorkerFailed, label + " leaves its folder.");
                }

                try
                {
                    var failure = await CopyOneAsync(source, destination, file, label).ConfigureAwait(false);
                    if (failure is not null)
                    {
                        return failure;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return (RunErrorCodes.DownloadFailed, label + " could not be copied: " + ex.GetType().Name + ".");
                }
            }
        }

        return null;
    }

    private static async Task<(string Code, string Detail)?> CopyOneAsync(string source, string destination, WorkerResultFile file, string label)
    {
        if (!File.Exists(source))
        {
            return (RunErrorCodes.WorkerFailed, label + " is listed by the worker but is not in the job folder.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var partial = destination + ".part";
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long written = 0;
            await using (var from = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
            await using (var to = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await from.ReadAsync(buffer).ConfigureAwait(false)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await to.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                    written += read;
                }
            }

            if (file.Bytes is { } expectedBytes && expectedBytes != written)
            {
                return (RunErrorCodes.DownloadCorrupt, $"{label} is {written} bytes, the worker said {expectedBytes}.");
            }

            if (file.Sha256 is { } expectedHash && !string.Equals(Convert.ToHexString(hash.GetHashAndReset()), expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                return (RunErrorCodes.DownloadCorrupt, label + " does not match the checksum the worker reported.");
            }

            File.Move(partial, destination, overwrite: true);
            return null;
        }
        finally
        {
            try
            {
                File.Delete(partial);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A leftover .part never carries the final name.
            }
        }
    }

    /// <summary>A result path must be <c>output/&lt;relative&gt;</c> with plain segments; null otherwise (the same rule the cloud download applies).</summary>
    private static string? SafeRelativeOutputPath(string path)
    {
        const string Root = "output/";
        if (!path.StartsWith(Root, StringComparison.Ordinal) || path.Contains('\\', StringComparison.Ordinal) || path.Contains(':', StringComparison.Ordinal))
        {
            return null;
        }

        var invalid = Path.GetInvalidFileNameChars();
        var segments = path[Root.Length..].Split('/');
        return segments.Any(seg => seg.Length == 0 || seg is "." or ".." || seg.IndexOfAny(invalid) >= 0)
            ? null
            : string.Join(Path.DirectorySeparatorChar, segments);
    }

    private async Task RemoveEmptyFolderAsync(string jobId, string runFolder)
    {
        try
        {
            if (Directory.Exists(runFolder) && !Directory.EnumerateFileSystemEntries(runFolder).Any())
            {
                Directory.Delete(runFolder, recursive: false);
                await UpdateRowAsync(jobId, row => row with { OutputDir = null }).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Clutter, not a correctness problem: the run's own failure is what gets recorded.
        }
    }

    private async Task<LocalJobResult> FailAsync(string jobId, string code, string detail)
    {
        await SetPhaseAsync(jobId, JobPhase.Failed, code, detail).ConfigureAwait(false);
        return new LocalJobResult(JobPhase.Failed, code, detail);
    }

    private async Task<RunRecord?> LatestAsync(string jobId)
    {
        var all = await _runs.GetAllAsync(CancellationToken.None).ConfigureAwait(false);
        return all.LastOrDefault(r => r.JobId == jobId);
    }

    private async Task UpdateRowAsync(string jobId, Func<RunRecord, RunRecord> change)
    {
        var existing = await LatestAsync(jobId).ConfigureAwait(false);
        if (existing is not null)
        {
            await _runs.UpsertAsync(change(existing), CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>Writes a phase to the row, THEN tells the listener (a phase is committed before the UI hears of it). A run already at a terminal phase never moves.</summary>
    private async Task SetPhaseAsync(string jobId, JobPhase phase, string? errorCode = null, string? errorDetail = null)
    {
        var existing = await LatestAsync(jobId).ConfigureAwait(false);
        if (existing is not null && (existing.Phase == phase || JobStateMachine.IsTerminal(existing.Phase)))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var next = (existing ?? new RunRecord(jobId, phase, now, Target: RunTargets.LocalRecord)) with { Phase = phase };
        if (JobStateMachine.IsTerminal(phase))
        {
            next = next with { FinishedAt = now };
        }

        if (errorCode is not null)
        {
            next = next with { ErrorCode = errorCode, ErrorDetail = errorDetail };
        }

        await _runs.UpsertAsync(next, CancellationToken.None).ConfigureAwait(false);
        try
        {
            _onPhaseChanged?.Invoke(jobId, phase);
        }
        catch (Exception)
        {
            // A listener's failure is its own and never stops a run.
        }
    }
}
