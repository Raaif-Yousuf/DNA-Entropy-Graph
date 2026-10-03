namespace DnaEntropyGraph.Cloud;

/// <summary>
/// What <see cref="FakeGcp"/>'s simulated worker does once a VM exists for a job whose
/// <c>manifest.json</c> is in the fake bucket (issue #460). The real worker is
/// <c>worker/</c>; this reproduces only the contract surface the app reads
/// (<c>result.json</c> and the files it lists, docs/job_contract.md section 7).
/// </summary>
public enum FakeWorkerMode
{
    /// <summary>Every input finishes: one track per input and a result.json listing it with a real checksum.</summary>
    Done,

    /// <summary>The worker never writes result.json (a hung or lost worker).</summary>
    Never,

    /// <summary>The job reports <c>failed</c> with <c>MODEL_OOM</c> and no inputs finished.</summary>
    WholeJobFailed,

    /// <summary>The job is <c>done</c> but every input is <c>failed</c>.</summary>
    AllInputsFailed,

    /// <summary>The first input finishes and every later one fails (needs at least two inputs).</summary>
    SecondInputFailed,

    /// <summary>The listed checksum does not match the uploaded bytes.</summary>
    ChecksumMismatch,

    /// <summary>A listed path climbs out of <c>output/</c> with <c>..</c>.</summary>
    UnsafePath,

    /// <summary>result.json exists but is not JSON.</summary>
    GarbageResult,

    /// <summary>Every input reports <c>done</c> but lists no files (a contract violation: a finished input has a track).</summary>
    DoneWithNoFiles,

    /// <summary>The GPU never came up: status.json reports <c>GPU_NOT_VISIBLE</c>, no result.json, and the VM is deleted.</summary>
    GpuNotVisible,

    /// <summary>The worker image could not be pulled: status.json reports <c>IMAGE_PULL_FAILED</c>, no result.json.</summary>
    ImagePullFailed,

    /// <summary>The manifest could not be downloaded on the VM: status.json reports <c>MANIFEST_INVALID</c>, no result.json.</summary>
    ManifestInvalid,

    /// <summary>The startup script hit an uncaught error: status.json reports <c>WORKER_CRASH</c>, no result.json.</summary>
    WorkerCrash,
}

/// <summary>Whether the simulated worker ends its own VM after writing result.json (worker exit codes 10 and 11 in <c>worker/vm/startup.sh</c>).</summary>
public enum WorkerSelfEnd
{
    /// <summary>The worker leaves the VM RUNNING; the runner ends it.</summary>
    None,

    /// <summary>The worker stops its own VM.</summary>
    Stop,

    /// <summary>The worker deletes its own VM.</summary>
    Delete,
}
