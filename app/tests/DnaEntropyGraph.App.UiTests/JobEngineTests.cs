using System.Collections.Concurrent;
using CommunityToolkit.Mvvm.Messaging;
using DnaEntropyGraph.Cloud;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Inputs;
using DnaEntropyGraph.Core.LocalEngine;
using DnaEntropyGraph.LocalEngine;
using DnaEntropyGraph.Persistence;
using DnaEntropyGraph.Presentation.Messaging;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.App.UiTests;

/// <summary>
/// Issue #428. JobEngine is a plain class with no WinUI window, so these run
/// without an interactive session. They prove the seam ShellViewModel's
/// tests stop at: a run started through the real <see cref="JobEngine"/>
/// reaches <see cref="CloudJobRunner"/>, which reaches the gateway (FakeGcp
/// records the VM), and every phase the runner records reaches the messenger.
/// </summary>
public class JobEngineTests
{
    private const string Project = "my-project";

    private static readonly string Pinned = "ghcr.io/raaif-yousuf/dna-entropy-worker:0.1.0-cuda@sha256:" + new string('a', 64);

    private static readonly string TempRoot = CreateTempRoot();

    private static string CreateTempRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "deg-engine-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        };
        return path;
    }

    private static string UserInputPath(string name = "SetTnpB.gb")
    {
        var dir = Path.Combine(TempRoot, "user");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        if (!File.Exists(path))
        {
            File.WriteAllText(path, "LOCUS       SetTnpB\nORIGIN\n        1 acgtacgtac\n//\n");
        }

        return path;
    }

    private sealed class StaticImageProvider(WorkerImageResolution resolution) : IWorkerImageProvider
    {
        public List<(string Version, bool Gpu)> Calls { get; } = [];

        public WorkerImageResolution Resolve(string appVersion, bool gpu)
        {
            Calls.Add((appVersion, gpu));
            return resolution;
        }
    }

    private sealed class MemorySettings : ISettingsStore
    {
        private readonly ConcurrentDictionary<string, string> _values = new();

        /// <summary>When set, every read throws it (a persistently locked or unusable settings store, #558).</summary>
        public IOException? FailReadsWith { get; set; }

        public string? GetString(string key)
            => FailReadsWith is not null ? throw FailReadsWith : _values.TryGetValue(key, out var v) ? v : null;

        public void SetString(string key, string value) => _values[key] = value;
    }

    private sealed class MemoryRuns : IRunRepository
    {
        private readonly ConcurrentDictionary<string, RunRecord> _rows = new();

        public Task<IReadOnlyList<RunRecord>> GetAllAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<RunRecord>>(_rows.Values.ToList());

        public Task UpsertAsync(RunRecord run, CancellationToken cancellationToken)
        {
            _rows[run.JobId] = run;
            return Task.CompletedTask;
        }
    }

    private sealed class StaticReadiness(LocalReadiness readiness) : ILocalReadinessSource
    {
        public int Checks { get; private set; }

        public Task<LocalReadiness> CheckAsync(CancellationToken cancellationToken)
        {
            Checks++;
            return Task.FromResult(readiness);
        }
    }

    private sealed class NoProcessLauncher : IProcessLauncher
    {
        public IRunningProcess Start(ProcessSpec spec, Action<string>? onOutputLine = null) => throw new ProcessStartFailedException(spec.FileName);
    }

    /// <summary>A worker that finishes at once and writes a result listing one good track, the way the real mock worker does.</summary>
    private sealed class FinishingWorkerLauncher : IProcessLauncher
    {
        public List<ProcessSpec> Started { get; } = [];

        public IRunningProcess Start(ProcessSpec spec, Action<string>? onOutputLine = null)
        {
            Started.Add(spec);
            var root = spec.Arguments[spec.Arguments.ToList().IndexOf("--root") + 1];
            Directory.CreateDirectory(Path.Combine(root, "output", "SetTnpB"));
            File.WriteAllText(Path.Combine(root, "output", "SetTnpB", "SetTnpB.entropy.bedgraph"), "x 0 10 1.0\n");
            File.WriteAllText(
                Path.Combine(root, "result.json"),
                "{\"schema\":1,\"status\":\"done\",\"inputs\":[{\"id\":\"in1\",\"status\":\"done\",\"files\":[{\"path\":\"output/SetTnpB/SetTnpB.entropy.bedgraph\"}]}]}");
            return new Finished();
        }

        private sealed class Finished : IRunningProcess
        {
            public Task<int> Exited { get; } = Task.FromResult(0);

            public void Kill(bool entireTree)
            {
            }

            public void Dispose()
            {
            }
        }
    }

    private sealed class HangingLauncher : IProcessLauncher
    {
        public TaskCompletionSource<int> Exit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Killed { get; private set; }

        public IRunningProcess Start(ProcessSpec spec, Action<string>? onOutputLine = null) => new Hanging(this);

        private sealed class Hanging(HangingLauncher owner) : IRunningProcess
        {
            public Task<int> Exited => owner.Exit.Task;

            public void Kill(bool entireTree)
            {
                owner.Killed = true;
                owner.Exit.TrySetResult(-1);
            }

            public void Dispose()
            {
            }
        }
    }

    private static LocalReadiness HealthyPc(bool gpu)
        => new(
            gpu ? new LocalCapability(true, [], new GpuInfo("NVIDIA GeForce RTX 4090", 24564, "551.61"), 100L << 30) : new LocalCapability(false, [LocalBlocker.NoNvidiaGpu], null, 100L << 30),
            new LocalEngineHealth(LocalEngineState.Healthy, "0.0.1", gpu, gpu, string.Empty));

    private sealed class MemoryProjects : IProjectRepository
    {
        public Task<IReadOnlyList<ProjectRecord>> GetAllAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ProjectRecord>>([]);

        public Task UpsertAsync(ProjectRecord project, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task EnsureAsync(string projectId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class Harness
    {
        public Harness(FakeGcp gcp, WorkerImageResolution? image = null, Func<string?>? downloads = null, LocalReadiness? readiness = null, IProcessLauncher? launcher = null)
        {
            ImageProvider = new StaticImageProvider(image ?? new WorkerImageResolution(WorkerImageStatus.Available, Pinned));
            AppData = Path.Combine(TempRoot, "appdata-" + Guid.NewGuid().ToString("N"));
            Gcp = gcp;
            Messenger = new WeakReferenceMessenger();
            Runs = new MemoryRuns();
            Settings = new MemorySettings();
            Messenger.Register<Harness, RunPhaseChangedMessage>(this, (_, m) =>
            {
                Messages.Enqueue(m);
                if (JobStateMachine.IsTerminal(m.Phase))
                {
                    Terminal.TrySetResult(m.Phase);
                }
            });
            var runner = new CloudJobRunner(gcp, gcp, gcp, gcp, gcp, Runs, (id, phase) => Messenger.Send(new RunPhaseChangedMessage(id, phase)))
            {
                ResultPollInterval = TimeSpan.FromMilliseconds(1),
                ResultTimeout = TimeSpan.FromSeconds(5),
            };
            var activeRuns = new ActiveRuns();
            ActiveRuns = activeRuns;
            var ready = new StaticReadiness(readiness ?? new LocalReadiness(new LocalCapability(false, [LocalBlocker.NoNvidiaGpu], null, 0), LocalEngineHealth.NotInstalled()));
            var inputs = new LocalRunInputStore(AppData);
            var localRunner = new LocalJobRunner(
                launcher ?? new NoProcessLauncher(),
                Runs,
                new LocalEngineLayout(Path.Combine(AppData, "engine")),
                Path.Combine(AppData, "runs"),
                (id, phase) => Messenger.Send(new RunPhaseChangedMessage(id, phase)))
            {
                PollInterval = TimeSpan.FromMilliseconds(10),
                CancelGrace = TimeSpan.FromMilliseconds(100),
            };
            Ready = ready;
            var local = new LocalRunDriver(Messenger, localRunner, ready, Settings, Runs, inputs, activeRuns, downloads);
            Local = local;
            Engine = new JobEngine(Messenger, runner, gcp, Settings, Runs, new MemoryProjects(), inputs, ImageProvider, activeRuns, new RunTargetResolver(ready), local, downloads);
        }

        public StaticImageProvider ImageProvider { get; }

        public StaticReadiness Ready { get; }

        public LocalRunDriver Local { get; }

        public ActiveRuns ActiveRuns { get; }

        public string AppData { get; }

        public FakeGcp Gcp { get; }

        public WeakReferenceMessenger Messenger { get; }

        public MemoryRuns Runs { get; }

        public MemorySettings Settings { get; }

        public JobEngine Engine { get; }

        public ConcurrentQueue<RunPhaseChangedMessage> Messages { get; } = new();

        public TaskCompletionSource<JobPhase> Terminal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<JobPhase> TerminalAsync() => await Terminal.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static RunOptions Options(string target = "Cloud", AfterTaskAction after = AfterTaskAction.Stop, string? input = null, string? output = null)
        => new()
        {
            ModelId = "evo2_7b",
            RunTarget = target,
            AfterTask = after,
            InputPath = input ?? UserInputPath(),
            OutputFolder = output ?? Path.Combine(TempRoot, "out-" + Guid.NewGuid().ToString("N")),
        };

    [Fact]
    public async Task Starting_a_cloud_run_reaches_the_gateway_and_completes()
    {
        var h = new Harness(new FakeGcp().WithSelectedProject(Project));

        var jobId = await h.Engine.StartRunAsync(Options(), CancellationToken.None);
        var terminal = await h.TerminalAsync();

        terminal.ShouldBe(JobPhase.Completed);
        h.Messages.Select(m => m.Phase).ShouldContain(JobPhase.Provisioning);
        h.Messages.ShouldAllBe(m => m.JobId == jobId);
        var vms = await h.Gcp.FindByJobIdAsync(jobId, CancellationToken.None);
        vms.Count.ShouldBe(1, "the observable: FakeGcp recorded a CreateVmAsync for this job id");
        vms[0].Status.ShouldBe("STOPPED", "Hard Rule 11 default after a run is Stop");
    }

    [Fact]
    public async Task The_run_row_is_written_up_front_and_keeps_its_columns()
    {
        var h = new Harness(new FakeGcp().WithSelectedProject(Project));

        var jobId = await h.Engine.StartRunAsync(Options(), CancellationToken.None);
        await h.TerminalAsync();

        var row = (await h.Runs.GetAllAsync(CancellationToken.None)).Single(r => r.JobId == jobId);
        row.Phase.ShouldBe(JobPhase.Completed);
        row.ProjectId.ShouldBe(Project);
        row.InstallationId.ShouldBe(InstallationId.GetOrCreate(h.Settings));
        row.OptionsJson.ShouldContain("evo2_7b");
        row.Target.ShouldBe("cloud");
        row.VmName.ShouldBe((await h.Gcp.FindByJobIdAsync(jobId, CancellationToken.None)).Single().Name, "the row names the VM the run used (issue #679)");
    }

    [Fact]
    public async Task A_run_with_no_selected_project_fails_with_a_recorded_reason_and_creates_nothing()
    {
        var h = new Harness(new FakeGcp().WithSignedOut());

        var jobId = await h.Engine.StartRunAsync(Options(), CancellationToken.None);

        (await h.TerminalAsync()).ShouldBe(JobPhase.Failed);
        var row = (await h.Runs.GetAllAsync(CancellationToken.None)).Single(r => r.JobId == jobId);
        row.Phase.ShouldBe(JobPhase.Failed);
        row.ErrorCode.ShouldBe("no_project");
        (await h.Gcp.FindByJobIdAsync(jobId, CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_target_other_than_cloud_fails_visibly_instead_of_silently_running_in_the_cloud()
    {
        var h = new Harness(new FakeGcp().WithSelectedProject(Project));

        var jobId = await h.Engine.StartRunAsync(Options(target: "LocalPc"), CancellationToken.None);

        (await h.TerminalAsync()).ShouldBe(JobPhase.Failed);
        (await h.Runs.GetAllAsync(CancellationToken.None)).Single(r => r.JobId == jobId).ErrorCode.ShouldBe("target_not_supported");
        (await h.Gcp.FindByJobIdAsync(jobId, CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task The_pinned_worker_image_reaches_the_VM_as_startup_metadata_without_any_setting()
    {
        var h = new Harness(new FakeGcp().WithSelectedProject(Project));

        var jobId = await h.Engine.StartRunAsync(Options(), CancellationToken.None);
        (await h.TerminalAsync()).ShouldBe(JobPhase.Completed);

        h.Settings.GetString("worker_image").ShouldBeNull("nobody set it; the app supplied the image");
        var metadata = h.Gcp.CreatedSpecs.Single().Metadata;
        metadata.ShouldNotBeNull();
        metadata!["deg-job-id"].ShouldBe(jobId);
        metadata["deg-worker-image"].ShouldBe(Pinned);
        metadata.ShouldContainKey("startup-script");
        h.ImageProvider.Calls.Single().Gpu.ShouldBeTrue("a GPU tier asks for the -cuda image");
    }

    [Fact]
    public async Task With_no_image_shipped_the_run_is_Failed_with_a_named_code_and_no_VM_is_created()
    {
        var h = new Harness(new FakeGcp().WithSelectedProject(Project), new WorkerImageResolution(WorkerImageStatus.NoneShipped, null));

        var jobId = await h.Engine.StartRunAsync(Options(), CancellationToken.None);

        (await h.TerminalAsync()).ShouldBe(JobPhase.Failed);
        var row = (await h.Runs.GetAllAsync(CancellationToken.None)).Single(r => r.JobId == jobId);
        row.Phase.ShouldBe(JobPhase.Failed);
        row.ErrorCode.ShouldBe(RunErrorCodes.WorkerImageUnavailable);
        h.Gcp.CreatedSpecs.ShouldBeEmpty();
        (await h.Gcp.FindByJobIdAsync(jobId, CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_refused_image_override_is_Failed_with_its_own_code_and_no_VM_is_created()
    {
        var h = new Harness(new FakeGcp().WithSelectedProject(Project), new WorkerImageResolution(WorkerImageStatus.OverrideRefused, null));

        var jobId = await h.Engine.StartRunAsync(Options(), CancellationToken.None);

        (await h.TerminalAsync()).ShouldBe(JobPhase.Failed);
        (await h.Runs.GetAllAsync(CancellationToken.None)).Single(r => r.JobId == jobId).ErrorCode.ShouldBe(RunErrorCodes.WorkerImageRefused);
        h.Gcp.CreatedSpecs.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("")]
    public async Task A_blank_output_folder_means_the_default_folder_and_the_run_still_gets_a_row(string blank)
    {
        var downloads = Path.Combine(TempRoot, "dl-" + Guid.NewGuid().ToString("N"));
        var h = new Harness(new FakeGcp().WithSelectedProject(Project), downloads: () => downloads);

        var jobId = await h.Engine.StartRunAsync(Options() with { OutputFolder = blank }, CancellationToken.None);
        (await h.TerminalAsync()).ShouldBe(JobPhase.Completed);

        var row = (await h.Runs.GetAllAsync(CancellationToken.None)).Single(r => r.JobId == jobId);
        Path.GetDirectoryName(row.OutputDir!).ShouldBe(downloads);
    }

    [Fact]
    public async Task An_input_with_an_invalid_character_fails_locally_and_creates_nothing_in_the_cloud()
    {
        var h = new Harness(new FakeGcp().WithSelectedProject(Project));
        var path = Path.Combine(TempRoot, "user", "bad-" + Guid.NewGuid().ToString("N") + ".fasta");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, ">secretname\nACGTACGTAC\nACGXACGTAC\n");

        var jobId = await h.Engine.StartRunAsync(Options(input: path), CancellationToken.None);

        (await h.TerminalAsync()).ShouldBe(JobPhase.Failed);
        var row = (await h.Runs.GetAllAsync(CancellationToken.None)).Single(r => r.JobId == jobId);
        row.ErrorCode.ShouldBe(RunErrorCodes.InputInvalidCharacter);
        row.ErrorDetail.ShouldNotBeNull();
        row.ErrorDetail.ShouldContain("record=1");
        row.ErrorDetail.ShouldNotContain("secretname", Case.Insensitive);
        h.Gcp.CreateAttempts.ShouldBe(0);
        h.Gcp.CreatedSpecs.ShouldBeEmpty();
        h.Gcp.ObjectKeys(FakeGcp.BucketName(Project)).ShouldBeEmpty();
    }

    private static string PairFiles(string fastaSeqName, string gffSeqName, out string gff)
    {
        var dir = Path.Combine(TempRoot, "user-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var fasta = Path.Combine(dir, "pair.fasta");
        File.WriteAllText(fasta, ">" + fastaSeqName + "\nACGTACGTACGTACGTACGT\n");
        gff = Path.Combine(dir, "pair.gff3");
        File.WriteAllText(gff, "##gff-version 3\n" + gffSeqName + "\tsrc\tgene\t1\t9\t.\t+\t.\tID=g1\n");
        return fasta;
    }

    [Fact]
    public async Task A_paired_gff3_is_staged_beside_the_input_checked_and_uploaded_with_it()
    {
        var h = new Harness(new FakeGcp().WithSelectedProject(Project));
        var fasta = PairFiles("chr1", "chr1", out var gff);

        var jobId = await h.Engine.StartRunAsync(Options(input: fasta) with { AnnotationPath = gff }, CancellationToken.None);

        (await h.TerminalAsync()).ShouldBe(JobPhase.Completed);
        File.Exists(Path.Combine(h.AppData, "runs", jobId, "annotation", "pair.gff3")).ShouldBeTrue();
        h.Gcp.ObjectKeys(FakeGcp.BucketName(Project)).ShouldContain(key => key.EndsWith("pair.gff3", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_paired_gff3_that_names_no_sequence_of_the_fasta_fails_locally_and_creates_nothing()
    {
        var h = new Harness(new FakeGcp().WithSelectedProject(Project));
        var fasta = PairFiles("chr1", "secretchrom", out var gff);

        var jobId = await h.Engine.StartRunAsync(Options(input: fasta) with { AnnotationPath = gff }, CancellationToken.None);

        (await h.TerminalAsync()).ShouldBe(JobPhase.Failed);
        var row = (await h.Runs.GetAllAsync(CancellationToken.None)).Single(r => r.JobId == jobId);
        row.ErrorCode.ShouldBe(RunErrorCodes.InputInvalid);
        row.ErrorDetail.ShouldNotBeNull().ShouldContain("NoNameMatch");
        row.ErrorDetail.ShouldNotContain("secretchrom", Case.Insensitive);
        h.Gcp.CreateAttempts.ShouldBe(0);
        h.Gcp.ObjectKeys(FakeGcp.BucketName(Project)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_paired_gff3_that_has_gone_missing_fails_as_a_missing_input_and_creates_nothing()
    {
        var h = new Harness(new FakeGcp().WithSelectedProject(Project));
        var fasta = PairFiles("chr1", "chr1", out var gff);
        File.Delete(gff);

        var jobId = await h.Engine.StartRunAsync(Options(input: fasta) with { AnnotationPath = gff }, CancellationToken.None);

        (await h.TerminalAsync()).ShouldBe(JobPhase.Failed);
        (await h.Runs.GetAllAsync(CancellationToken.None)).Single(r => r.JobId == jobId).ErrorCode.ShouldBe(RunErrorCodes.InputMissing);
        h.Gcp.CreateAttempts.ShouldBe(0);
    }

    [Fact]
    public async Task A_run_with_no_input_file_fails_with_input_missing_and_creates_nothing()
    {
        var h = new Harness(new FakeGcp().WithSelectedProject(Project));
        var options = Options() with { InputPath = null };

        var jobId = await h.Engine.StartRunAsync(options, CancellationToken.None);

        (await h.TerminalAsync()).ShouldBe(JobPhase.Failed);
        (await h.Runs.GetAllAsync(CancellationToken.None)).Single(r => r.JobId == jobId).ErrorCode.ShouldBe(RunErrorCodes.InputMissing);
        h.Gcp.CreatedSpecs.ShouldBeEmpty();
        h.Gcp.ObjectKeys(FakeGcp.BucketName(Project)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(typeof(SettingsUnavailableException))]
    [InlineData(typeof(InstallationIdUnusableException))]
    public async Task An_unavailable_or_unusable_installation_id_fails_the_run_before_any_cloud_resource_exists(Type failure)
    {
        var h = new Harness(new FakeGcp().WithSelectedProject(Project));
        h.Settings.FailReadsWith = failure == typeof(SettingsUnavailableException)
            ? new SettingsUnavailableException("settings problem")
            : new InstallationIdUnusableException("settings problem");

        var jobId = await h.Engine.StartRunAsync(Options(), CancellationToken.None);

        (await h.TerminalAsync()).ShouldBe(JobPhase.Failed);
        var row = (await h.Runs.GetAllAsync(CancellationToken.None)).Single(r => r.JobId == jobId);
        row.ErrorCode.ShouldBe(failure == typeof(SettingsUnavailableException) ? RunErrorCodes.Other : RunErrorCodes.InstallationIdUnusable);
        row.ErrorDetail.ShouldBe(failure.Name, "an error class only, never the message");
        h.Gcp.CreatedSpecs.ShouldBeEmpty();
        h.Gcp.ObjectKeys(FakeGcp.BucketName(Project)).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_input_file_that_no_longer_exists_fails_with_input_missing_and_creates_nothing()
    {
        var h = new Harness(new FakeGcp().WithSelectedProject(Project));

        var jobId = await h.Engine.StartRunAsync(Options(input: Path.Combine(TempRoot, "gone.gb")), CancellationToken.None);

        (await h.TerminalAsync()).ShouldBe(JobPhase.Failed);
        (await h.Runs.GetAllAsync(CancellationToken.None)).Single(r => r.JobId == jobId).ErrorCode.ShouldBe(RunErrorCodes.InputMissing);
        h.Gcp.CreatedSpecs.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_input_is_kept_under_app_data_the_bucket_holds_it_and_the_track_lands_in_the_chosen_folder()
    {
        var h = new Harness(new FakeGcp().WithSelectedProject(Project));
        var source = UserInputPath();
        var before = File.ReadAllBytes(source);
        var output = Path.Combine(TempRoot, "out-observable");

        var jobId = await h.Engine.StartRunAsync(Options(input: source, output: output), CancellationToken.None);
        (await h.TerminalAsync()).ShouldBe(JobPhase.Completed);

        // Hard Rule 14: a copy under app data, the original untouched, nothing written next to it.
        File.ReadAllBytes(Path.Combine(h.AppData, "runs", jobId, "input", "SetTnpB.gb")).ShouldBe(before);
        File.ReadAllBytes(source).ShouldBe(before);
        Directory.GetFiles(Path.GetDirectoryName(source)!, "*.bedgraph").ShouldBeEmpty();

        // The observable for #460: the bucket holds the real bytes and the manifest...
        var bucket = FakeGcp.BucketName(Project);
        h.Gcp.GetObjectBytes(bucket, $"jobs/{jobId}/input/SetTnpB.gb").ShouldBe(before);
        h.Gcp.ObjectKeys(bucket).ShouldContain($"jobs/{jobId}/manifest.json");

        // ...and the output folder holds the track.
        var row = (await h.Runs.GetAllAsync(CancellationToken.None)).Single(r => r.JobId == jobId);
        Path.GetDirectoryName(row.OutputDir!).ShouldBe(output);
        File.ReadAllBytes(Path.Combine(row.OutputDir!, "SetTnpB", "SetTnpB.bedgraph")).ShouldBe(FakeGcp.TrackBytes("SetTnpB"));
    }

    [Fact]
    public async Task The_manifest_and_the_run_row_name_the_pinned_image()
    {
        var h = new Harness(new FakeGcp().WithSelectedProject(Project));

        var jobId = await h.Engine.StartRunAsync(Options(), CancellationToken.None);
        await h.TerminalAsync();

        var row = (await h.Runs.GetAllAsync(CancellationToken.None)).Single(r => r.JobId == jobId);
        row.ManifestJson.ShouldNotBeNull();
        row.ManifestJson!.ShouldContain(Pinned);
        row.WorkerImageDigest.ShouldBe(Pinned);
    }

    [Fact]
    public async Task A_preflight_failure_surfaces_as_a_Failed_phase()
    {
        var h = new Harness(new FakeGcp().WithSelectedProject(Project).WithBillingOff(Project));

        await h.Engine.StartRunAsync(Options(), CancellationToken.None);

        (await h.TerminalAsync()).ShouldBe(JobPhase.Failed);
    }

    [Fact]
    public async Task Stopping_the_VM_reaches_the_real_gateway()
    {
        var h = new Harness(new FakeGcp().WithSelectedProject(Project));
        var jobId = await h.Engine.StartRunAsync(Options(after: AfterTaskAction.KeepAlive), CancellationToken.None);
        await h.TerminalAsync();
        (await h.Gcp.FindByJobIdAsync(jobId, CancellationToken.None))[0].Status.ShouldBe("RUNNING");

        await h.Engine.StopVmAsync(jobId, CancellationToken.None);

        (await h.Gcp.FindByJobIdAsync(jobId, CancellationToken.None))[0].Status.ShouldBe("STOPPED");
    }

    [Fact]
    public async Task Deleting_the_VM_reaches_the_real_gateway()
    {
        var h = new Harness(new FakeGcp().WithSelectedProject(Project));
        var jobId = await h.Engine.StartRunAsync(Options(after: AfterTaskAction.KeepAlive), CancellationToken.None);
        await h.TerminalAsync();

        await h.Engine.DeleteVmAsync(jobId, CancellationToken.None);

        (await h.Gcp.FindByJobIdAsync(jobId, CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Cancelling_a_run_blocked_in_CreateVm_ends_Cancelled_with_no_VM()
    {
        var h = new Harness(new FakeGcp().WithSelectedProject(Project).WithBlockedCreate());
        var jobId = await h.Engine.StartRunAsync(Options(), CancellationToken.None);
        await h.Gcp.CreateVmEntered.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);

        await h.Engine.CancelRunAsync(jobId, CancellationToken.None);

        (await h.TerminalAsync()).ShouldBe(JobPhase.Cancelled);
        (await h.Runs.GetAllAsync(CancellationToken.None)).Single(r => r.JobId == jobId).Phase.ShouldBe(JobPhase.Cancelled);
        (await h.Gcp.FindByJobIdAsync(jobId, CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Cancelling_while_a_create_that_ignores_its_token_completes_still_leaves_no_VM()
    {
        // The create was already accepted by the API: it finishes after the
        // caller gave up. The run must not walk on to Preparing, and the
        // cancel must delete the VM that appeared.
        var h = new Harness(new FakeGcp().WithSelectedProject(Project).WithBlockedCreate(ignoreCancellation: true));
        var jobId = await h.Engine.StartRunAsync(Options(), CancellationToken.None);
        await h.Gcp.CreateVmEntered.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);

        var cancel = h.Engine.CancelRunAsync(jobId, CancellationToken.None);

        // The insert is still in flight, so the cancel must still be waiting: finishing now would
        // mean it looked for a VM that does not exist yet and gave up (a billed VM left behind).
        await Task.WhenAny(cancel, Task.Delay(TimeSpan.FromMilliseconds(300), CancellationToken.None));
        cancel.IsCompleted.ShouldBeFalse("a cancel must wait for an insert that is still in flight");

        h.Gcp.ReleaseCreate();
        await cancel.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);

        (await h.TerminalAsync()).ShouldBe(JobPhase.Cancelled);
        (await h.Runs.GetAllAsync(CancellationToken.None)).Single(r => r.JobId == jobId).Phase.ShouldBe(JobPhase.Cancelled);
        h.Messages.Select(m => m.Phase).ShouldNotContain(JobPhase.Preparing);
        (await h.Gcp.FindByJobIdAsync(jobId, CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Cancelling_immediately_after_start_leaves_exactly_one_terminal_state()
    {
        // The window between registering the run and its task starting used
        // to let a cancel see no task and write the row concurrently.
        for (var i = 0; i < 20; i++)
        {
            var h = new Harness(new FakeGcp().WithSelectedProject(Project));
            var jobId = await h.Engine.StartRunAsync(Options(), CancellationToken.None);
            await h.Engine.CancelRunAsync(jobId, CancellationToken.None);

            var phase = (await h.Runs.GetAllAsync(CancellationToken.None)).Single(r => r.JobId == jobId).Phase;
            JobStateMachine.IsTerminal(phase).ShouldBeTrue($"iteration {i} ended in {phase}");
            (await h.Gcp.FindByJobIdAsync(jobId, CancellationToken.None)).Count(v => v.Status == "RUNNING").ShouldBe(0);
        }
    }

    [Fact]
    public async Task A_pre_start_failure_records_a_code_and_no_English_detail()
    {
        var h = new Harness(new FakeGcp().WithSignedOut());

        var jobId = await h.Engine.StartRunAsync(Options(), CancellationToken.None);
        await h.TerminalAsync();

        var row = (await h.Runs.GetAllAsync(CancellationToken.None)).Single(r => r.JobId == jobId);
        row.ErrorCode.ShouldBe(RunErrorCodes.NoProject);
        row.ErrorDetail.ShouldBeNull();
    }

    [Fact]
    public async Task Cancelling_an_unknown_job_id_publishes_nothing_and_does_not_throw()
    {
        var h = new Harness(new FakeGcp().WithSelectedProject(Project));

        await Should.NotThrowAsync(() => h.Engine.CancelRunAsync("no-such-job", CancellationToken.None));

        h.Messages.ShouldBeEmpty();
    }

    [Fact]
    public async Task Cancelling_a_finished_run_changes_nothing()
    {
        var h = new Harness(new FakeGcp().WithSelectedProject(Project));
        var jobId = await h.Engine.StartRunAsync(Options(), CancellationToken.None);
        await h.TerminalAsync();
        var before = h.Messages.Count;

        await h.Engine.CancelRunAsync(jobId, CancellationToken.None);

        h.Messages.Count.ShouldBe(before);
    }

    [Fact]
    public async Task This_PC_with_a_healthy_engine_runs_the_worker_locally_with_no_cloud_resource_and_no_cloud_phases()
    {
        var worker = new FinishingWorkerLauncher();
        var h = new Harness(new FakeGcp().WithSelectedProject(Project), readiness: HealthyPc(gpu: false), launcher: worker);

        var jobId = await h.Engine.StartRunAsync(Options(target: "This PC") with { Predictor = PredictorSelection.MockTestPredictor }, CancellationToken.None);

        var phase = await h.TerminalAsync();
        var failed = (await h.Runs.GetAllAsync(CancellationToken.None)).Single(r => r.JobId == jobId);
        phase.ShouldBe(JobPhase.Completed, failed.ErrorCode + " " + failed.ErrorDetail);
        worker.Started.Single().Arguments.ShouldContain("localdir");
        (await h.Gcp.FindByJobIdAsync(jobId, CancellationToken.None)).ShouldBeEmpty("the observable: a This PC run creates nothing in the cloud");
        h.Messages.Select(m => m.Phase).ShouldNotContain(JobPhase.Provisioning);
        h.Messages.Select(m => m.Phase).ShouldNotContain(JobPhase.Uploading);
        var row = (await h.Runs.GetAllAsync(CancellationToken.None)).Single(r => r.JobId == jobId);
        row.Target.ShouldBe("local");
        row.OptionsJson.ShouldContain("This PC");
        Directory.EnumerateFiles(row.OutputDir!, "*.bedgraph", SearchOption.AllDirectories).ShouldNotBeEmpty();
    }

    [Fact]
    public async Task This_PC_with_no_engine_fails_with_an_action_instead_of_quietly_using_the_cloud()
    {
        var h = new Harness(new FakeGcp().WithSelectedProject(Project));

        var jobId = await h.Engine.StartRunAsync(Options(target: "This PC"), CancellationToken.None);

        (await h.TerminalAsync()).ShouldBe(JobPhase.Failed);
        var row = (await h.Runs.GetAllAsync(CancellationToken.None)).Single(r => r.JobId == jobId);
        row.ErrorCode.ShouldBe("local_engine_not_ready");
        row.Target.ShouldBe("local");
        (await h.Gcp.FindByJobIdAsync(jobId, CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task This_PC_with_a_real_model_and_no_nvidia_gpu_fails_with_the_gpu_code()
    {
        var h = new Harness(new FakeGcp().WithSelectedProject(Project), readiness: HealthyPc(gpu: false), launcher: new FinishingWorkerLauncher());

        var jobId = await h.Engine.StartRunAsync(Options(target: "This PC"), CancellationToken.None);

        (await h.TerminalAsync()).ShouldBe(JobPhase.Failed);
        (await h.Runs.GetAllAsync(CancellationToken.None)).Single(r => r.JobId == jobId).ErrorCode.ShouldBe("local_gpu_not_usable");
    }

    [Fact]
    public async Task This_PC_with_a_Hopper_only_model_fails_even_on_a_fast_gpu()
    {
        var h = new Harness(new FakeGcp().WithSelectedProject(Project), readiness: HealthyPc(gpu: true), launcher: new FinishingWorkerLauncher());

        var jobId = await h.Engine.StartRunAsync(Options(target: "This PC") with { ModelId = "evo2_20b" }, CancellationToken.None);

        (await h.TerminalAsync()).ShouldBe(JobPhase.Failed);
        (await h.Runs.GetAllAsync(CancellationToken.None)).Single(r => r.JobId == jobId).ErrorCode.ShouldBe("model_needs_hopper");
    }

    [Fact]
    public async Task Auto_runs_on_this_PC_when_it_is_ready_and_in_the_cloud_when_it_is_not()
    {
        var local = new Harness(new FakeGcp().WithSelectedProject(Project), readiness: HealthyPc(gpu: true), launcher: new FinishingWorkerLauncher());
        var localJob = await local.Engine.StartRunAsync(Options(target: "Auto"), CancellationToken.None);
        (await local.TerminalAsync()).ShouldBe(JobPhase.Completed);
        (await local.Runs.GetAllAsync(CancellationToken.None)).Single(r => r.JobId == localJob).Target.ShouldBe("local");

        var cloud = new Harness(new FakeGcp().WithSelectedProject(Project));
        var cloudJob = await cloud.Engine.StartRunAsync(Options(target: "Auto"), CancellationToken.None);
        (await cloud.TerminalAsync()).ShouldBe(JobPhase.Completed);
        (await cloud.Runs.GetAllAsync(CancellationToken.None)).Single(r => r.JobId == cloudJob).Target.ShouldBe("cloud");
    }

    [Fact]
    public async Task Cancelling_a_This_PC_run_stops_the_worker_and_records_Cancelled()
    {
        var hanging = new HangingLauncher();
        var h = new Harness(new FakeGcp().WithSelectedProject(Project), readiness: HealthyPc(gpu: false), launcher: hanging);
        var jobId = await h.Engine.StartRunAsync(Options(target: "This PC") with { Predictor = PredictorSelection.MockTestPredictor }, CancellationToken.None);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!h.Messages.Any(m => m.Phase == JobPhase.Running) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        await h.Engine.CancelRunAsync(jobId, CancellationToken.None);

        (await h.TerminalAsync()).ShouldBe(JobPhase.Cancelled);
        hanging.Killed.ShouldBeTrue("the worker ignored the cancel file, so its process tree was killed");
        (await h.Runs.GetAllAsync(CancellationToken.None)).Single(r => r.JobId == jobId).Phase.ShouldBe(JobPhase.Cancelled);
    }

    [Fact]
    public async Task Auto_that_picks_this_PC_looks_at_this_PC_once_not_twice()
    {
        // MEASURED 2026-10-15 (review of #173): the resolver checked, then the driver checked again, a second 60 s health run and a second chance to disagree.
        var h = new Harness(new FakeGcp().WithSelectedProject(Project), readiness: HealthyPc(gpu: true), launcher: new FinishingWorkerLauncher());

        await h.Engine.StartRunAsync(Options(target: "Auto"), CancellationToken.None);
        (await h.TerminalAsync()).ShouldBe(JobPhase.Completed);

        h.Ready.Checks.ShouldBe(1);
    }

    [Fact]
    public async Task An_unexpected_exception_in_a_This_PC_run_ends_the_row_Failed_with_a_code_instead_of_leaving_it_Running()
    {
        var h = new Harness(new FakeGcp().WithSelectedProject(Project), readiness: HealthyPc(gpu: false), launcher: new ThrowingLauncher());

        var jobId = await h.Engine.StartRunAsync(Options(target: "This PC") with { Predictor = PredictorSelection.MockTestPredictor }, CancellationToken.None);

        (await h.TerminalAsync()).ShouldBe(JobPhase.Failed);
        var row = (await h.Runs.GetAllAsync(CancellationToken.None)).Single(r => r.JobId == jobId);
        row.ErrorCode.ShouldBe("other");
        row.ErrorDetail.ShouldBe("InvalidOperationException");
        row.FinishedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_refused_start_ends_the_row_Failed_with_a_code()
    {
        var h = new Harness(new FakeGcp().WithSelectedProject(Project), readiness: HealthyPc(gpu: false), launcher: new FinishingWorkerLauncher());
        _ = h.ActiveRuns.TryStart("job-dup", token => Task.Delay(Timeout.Infinite, token));

        await h.Local.StartAsync("job-dup", Options(target: "This PC") with { Predictor = PredictorSelection.MockTestPredictor }, "0.0.1", CancellationToken.None);

        var row = (await h.Runs.GetAllAsync(CancellationToken.None)).Single(r => r.JobId == "job-dup");
        row.Phase.ShouldBe(JobPhase.Failed);
        row.ErrorCode.ShouldBe("other");
        row.FinishedAt.ShouldNotBeNull();
        await h.ActiveRuns.CancelAsync("job-dup", () => Task.CompletedTask);
    }

    [Fact]
    public async Task A_real_model_run_on_this_PC_pins_the_worker_to_the_card_that_was_judged()
    {
        var worker = new FinishingWorkerLauncher();
        var ready = new LocalReadiness(
            new LocalCapability(true, [], new GpuInfo("NVIDIA GeForce RTX 4090", 24564, "551.61", "GPU-abc"), 100L << 30),
            new LocalEngineHealth(LocalEngineState.Healthy, "0.0.1", true, true, string.Empty));
        var h = new Harness(new FakeGcp().WithSelectedProject(Project), readiness: ready, launcher: worker);

        await h.Engine.StartRunAsync(Options(target: "This PC"), CancellationToken.None);
        await h.TerminalAsync();

        worker.Started.Single().Environment!["CUDA_VISIBLE_DEVICES"].ShouldBe("GPU-abc");
    }

    private sealed class ThrowingLauncher : IProcessLauncher
    {
        public IRunningProcess Start(ProcessSpec spec, Action<string>? onOutputLine = null) => throw new InvalidOperationException("boom with a path C:\\Users\\someone");
    }
}
