using System.Globalization;
using System.Text;
using System.Text.Json;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Cloud;

namespace DnaEntropyGraph.CloudCli;

/// <summary>What the process exits with. A script branches on these, so each is a different cause with a different next step.</summary>
public static class CloudCliExitCode
{
    /// <summary>The command did what it was asked (an empty listing is success).</summary>
    public const int Ok = 0;

    /// <summary>Google refused or could not be reached, or the run was cancelled.</summary>
    public const int CloudError = 1;

    /// <summary>The command line is wrong, or <c>VmSpec</c> rejected the values (nothing was sent).</summary>
    public const int Usage = 2;

    /// <summary>No Google account is signed in (or its sign-in expired): sign in with the app first.</summary>
    public const int NotSignedIn = 3;

    /// <summary>A safety guard refused the action: a VM name without the <c>deg-</c> prefix, or a VM without the app label.</summary>
    public const int Refused = 4;

    /// <summary>The named thing is not there (a bucket for <c>bucket describe</c>).</summary>
    public const int NotFound = 5;
}

/// <summary>
/// The verbs, their tables and their exit codes (issue #60). Output is ASCII: a plain table (or <c>--json</c>) on stdout, an
/// <c>OK:</c> line when a command ended well, one <c>ERROR:</c> line on stderr when it did not, and each error names an action.
/// </summary>
public sealed class CloudCliApp
{
    private const string DelegatedPrefix = "deg-";
    private const string GpuTestModeKey = "deg-mode";
    private const int MaxObjectBytes = 4 * 1024 * 1024;
    private const string NotSignedInMessage = "Sign in with the app first (open DNA Entropy Graph and choose Sign in), then run this again.";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly ICloudCliSessionFactory _sessions;
    private readonly TimeProvider _clock;
    private readonly Random _random;

    public CloudCliApp(ICloudCliSessionFactory sessions, TimeProvider clock, Random random)
    {
        _sessions = sessions;
        _clock = clock;
        _random = random;
    }

    public async Task<int> RunAsync(IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        var parsed = CloudCliParser.Parse(args);
        if (parsed.Command is not { } command)
        {
            return Fail(stderr, CloudCliExitCode.Usage, parsed.Error ?? CloudCliParser.Usage);
        }

        // The deg- guard needs no network: refuse before a session (and so a token) exists.
        if (command is VmDeleteCommand { Name: var name } && !name.StartsWith(DelegatedPrefix, StringComparison.Ordinal))
        {
            return Fail(stderr, CloudCliExitCode.Refused, $"Refusing to delete '{name}': this tool only deletes VMs named {DelegatedPrefix}<job id>. Pass a name from 'resources list'.");
        }

        try
        {
            var install = command switch
            {
                ResourcesListCommand list => list.Install,
                VmCreateCommand create => create.Install,
                _ => null,
            };
            var session = _sessions.Open(command.Project, install);

            return command switch
            {
                ResourcesListCommand list => await ListAsync(session, list, stdout, cancellationToken).ConfigureAwait(false),
                BucketDescribeCommand describe => await DescribeAsync(session, describe, stdout, stderr, cancellationToken).ConfigureAwait(false),
                BucketEnsureCommand ensure => await EnsureBucketAsync(session, ensure, stdout, cancellationToken).ConfigureAwait(false),
                ObjectGetCommand get => await GetObjectAsync(session, get, stdout, stderr, cancellationToken).ConfigureAwait(false),
                VmCreateCommand create => await CreateAsync(session, create, stdout, stderr, cancellationToken).ConfigureAwait(false),
                VmDeleteCommand delete => await DeleteAsync(session, delete, stdout, stderr, cancellationToken).ConfigureAwait(false),
                _ => Fail(stderr, CloudCliExitCode.Usage, CloudCliParser.Usage),
            };
        }
        catch (CloudCliNotSignedInException)
        {
            return Fail(stderr, CloudCliExitCode.NotSignedIn, NotSignedInMessage);
        }
        catch (AccountAuthException ex)
        {
            return FailAuth(stderr, ex.Code);
        }
        catch (CloudOperationException ex)
        {
            return Fail(stderr, CloudCliExitCode.CloudError, $"Google Cloud refused or failed the call ({ex.Kind.ToString().ToLowerInvariant()}): {ex.Error.Message} {ActionFor(ex.Kind)}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Fail(stderr, CloudCliExitCode.CloudError, "Cancelled before it finished. Run it again, and check 'resources list' for anything half-made.");
        }
        catch (OperationCanceledException)
        {
            // HttpClient reports its own timeout as a TaskCanceledException while the caller's token is still live.
            return Fail(stderr, CloudCliExitCode.CloudError, "A Google call timed out. Check the internet connection and try again, and check 'resources list' for anything half-made.");
        }
        catch (Exception ex)
        {
            // Any other failure (a dropped connection, an unreadable token store, a bug) must not escape Main as a stack trace with
            // a runtime exit code a script cannot tell from a crash. Only the exception class is printed: the message can carry a
            // host name or a path.
            return Fail(stderr, CloudCliExitCode.CloudError, $"CloudCli stopped on an unexpected problem ({ex.GetType().Name}). Check the internet connection and that the app is signed in, then run this again.");
        }
    }

    /// <summary>Maps a sign-in failure by its code: only an expired or missing sign-in is "sign in with the app" (exit 3).</summary>
    private static int FailAuth(TextWriter stderr, string code) => code switch
    {
        AuthErrorCodes.SigninExpired or AuthErrorCodes.AccountNotFound or AuthErrorCodes.SigninFailed
            => Fail(stderr, CloudCliExitCode.NotSignedIn, "The app's Google sign-in has expired. Sign in with the app again, then run this again."),
        AuthErrorCodes.NetworkUnavailable
            => Fail(stderr, CloudCliExitCode.CloudError, "Google could not be reached to refresh the app's sign-in. Check the internet connection, then run this again."),
        AuthErrorCodes.OAuthClientMissing
            => Fail(stderr, CloudCliExitCode.CloudError, "This build has no Google sign-in client file (oauth_client.local.json). Put it in app/secrets, then run this again."),
        AuthErrorCodes.OAuthClientInvalid
            => Fail(stderr, CloudCliExitCode.CloudError, "The Google sign-in client file is not a Desktop-app client download. Download it again from the Google Cloud console, then run this again."),
        _ => Fail(stderr, CloudCliExitCode.CloudError, "The app's Google sign-in failed. Open the app, sign in again, then run this again."),
    };

    private static async Task<int> ListAsync(CloudCliSession session, ResourcesListCommand command, TextWriter stdout, CancellationToken cancellationToken)
    {
        var rows = new List<ResourceRow>();
        if (command.Kind != ResourceKind.Bucket)
        {
            var vms = command.Install is null
                ? await session.Compute.ListAppVmsAsync(cancellationToken).ConfigureAwait(false)
                : await session.Compute.ListByInstallationAsync(command.Install, cancellationToken).ConfigureAwait(false);
            rows.AddRange(vms.OrderBy(v => v.Name, StringComparer.Ordinal).Select(v => new ResourceRow("vm", v.Name, v.Zone, null, v.Status, Label(v.Labels, "installation-id"), Label(v.Labels, "job-id"))));
        }

        if (command.Kind != ResourceKind.Vm)
        {
            var buckets = await session.Storage.ListBucketsAsync(command.Project, cancellationToken).ConfigureAwait(false);
            // Not filtered by --install: the results bucket is shared and adopted across installations, so it carries its creator's
            // installation-id. The storage gateway already lists only buckets with the app label.
            rows.AddRange(buckets
                .OrderBy(b => b.Name, StringComparer.Ordinal)
                .Select(b => new ResourceRow("bucket", b.Name, null, b.Location, null, Label(b.Labels, "installation-id"), null)));
        }

        if (command.Json)
        {
            stdout.WriteLine(JsonSerializer.Serialize(
                rows.Select(r => new { kind = r.Kind, name = r.Name, zone = r.Zone, location = r.Location, status = r.Status, installation = r.Installation, job = r.Job }),
                JsonOptions));
            return CloudCliExitCode.Ok;
        }

        WriteTable(
            stdout,
            ["KIND", "NAME", "LOCATION", "STATUS", "INSTALLATION", "JOB"],
            rows.Select(r => new[] { r.Kind, r.Name, r.Zone ?? r.Location, r.Status, r.Installation, r.Job }));
        stdout.WriteLine($"OK: {rows.Count} resource(s)");
        return CloudCliExitCode.Ok;
    }

    private static async Task<int> DescribeAsync(CloudCliSession session, BucketDescribeCommand command, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        var all = await session.Storage.ListBucketsAsync(command.Project, cancellationToken).ConfigureAwait(false);
        var buckets = command.Name is null ? all : all.Where(b => b.Name == command.Name).ToList();
        if (buckets.Count == 0)
        {
            return Fail(
                stderr,
                CloudCliExitCode.NotFound,
                command.Name is null
                    ? $"No DNA Entropy Graph bucket exists in project {command.Project} yet. Run a job in the app first: it creates the bucket."
                    : $"No bucket named {command.Name} in project {command.Project}. Run 'resources list --project {command.Project} --kind bucket' to see the names.");
        }

        if (command.Json)
        {
            stdout.WriteLine(JsonSerializer.Serialize(
                buckets.Select(b => new { name = b.Name, location = b.Location, created = Created(b), labels = b.Labels.OrderBy(l => l.Key, StringComparer.Ordinal).ToDictionary(l => l.Key, l => l.Value) }),
                JsonOptions));
            return CloudCliExitCode.Ok;
        }

        foreach (var bucket in buckets)
        {
            stdout.WriteLine($"name: {Ascii(bucket.Name)}");
            stdout.WriteLine($"location: {Ascii(bucket.Location)}");
            stdout.WriteLine($"created: {Created(bucket) ?? "-"}");
            stdout.WriteLine($"labels: {Ascii(string.Join(",", bucket.Labels.OrderBy(l => l.Key, StringComparer.Ordinal).Select(l => $"{l.Key}={l.Value}")))}");
            stdout.WriteLine();
        }

        stdout.WriteLine($"OK: described {buckets.Count} bucket(s)");
        return CloudCliExitCode.Ok;
    }

    private static async Task<int> EnsureBucketAsync(CloudCliSession session, BucketEnsureCommand command, TextWriter stdout, CancellationToken cancellationToken)
    {
        var name = await session.Storage.EnsureBucketAsync(command.Project, cancellationToken).ConfigureAwait(false);
        stdout.WriteLine(command.Json
            ? JsonSerializer.Serialize(new { name }, JsonOptions)
            : $"OK: bucket {Ascii(name)}");
        return CloudCliExitCode.Ok;
    }

    private static async Task<int> GetObjectAsync(CloudCliSession session, ObjectGetCommand command, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        var stream = await session.Storage.TryDownloadAsync(command.Bucket, command.Key, cancellationToken).ConfigureAwait(false);
        if (stream is null)
        {
            return Fail(stderr, CloudCliExitCode.NotFound, $"{Ascii(command.Key)} is not there yet in bucket {Ascii(command.Bucket)}. Wait and run this again, or check the key with 'bucket describe'.");
        }

        await using (stream.ConfigureAwait(false))
        {
            // Capped: a log is text for a person, and one runaway object must not fill the console. Anything outside printable ASCII becomes '?'.
            var buffer = new byte[MaxObjectBytes + 1];
            var read = 0;
            while (read < buffer.Length)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);
                if (n == 0)
                {
                    break;
                }

                read += n;
            }

            var text = Encoding.UTF8.GetString(buffer, 0, Math.Min(read, MaxObjectBytes));
            var builder = new StringBuilder(text.Length);
            foreach (var c in text)
            {
                builder.Append(c is '\n' or '\r' or '\t' || c is >= ' ' and <= '~' ? c : '?');
            }

            stdout.Write(builder.ToString().Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", stdout.NewLine, StringComparison.Ordinal));
            if (read > MaxObjectBytes)
            {
                // Exit 0, not CloudError: the head WAS printed and a caller that saves stdout (cloud_gpu_test.ps1 pulls the log) must keep
                // it. The notice goes into the output itself, so a saved file says it is cut, and to stderr for the console.
                stdout.WriteLine();
                stdout.WriteLine($"[truncated: only the first {MaxObjectBytes} bytes are shown]");
                stderr.WriteLine($"WARNING: {Ascii(command.Key)} is larger than {MaxObjectBytes} bytes, so only the first {MaxObjectBytes} bytes were printed. Download the rest from the Google Cloud console.");
            }
        }

        return CloudCliExitCode.Ok;
    }

    private async Task<int> CreateAsync(CloudCliSession session, VmCreateCommand command, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        var jobId = command.Job ?? JobId.NewId(_clock.GetUtcNow(), _random);

        // Issue #759: with --worker-image the VM gets the startup script and its deg-* attributes (StartupMetadata validates every value
        // against the shape the script can consume). A test VM always deletes itself afterwards (AfterTaskAction.Delete).
        IReadOnlyDictionary<string, string>? metadata = null;
        if (command.WorkerImage is not null)
        {
            try
            {
                var built = new Dictionary<string, string>(StartupMetadata.Build(
                    command.Bucket!, jobId, command.WorkerImage, command.ExpectGpu, AfterTaskAction.Delete, command.MaxRunDuration));
                if (command.StartupMode == CloudCliParser.GpuTestMode)
                {
                    built[GpuTestModeKey] = CloudCliParser.GpuTestMode;
                }

                metadata = built;

                // Developer-mode carve-out (#759): the pinned worker image list ships embedded in the WinUI app, which this console host
                // cannot reference, so it cannot enforce the allowlist the app enforces. It says so every time instead of staying silent.
                stderr.WriteLine($"WARNING: --worker-image {Ascii(command.WorkerImage)} is not checked against the pinned worker image list that ships with the app (this is the developer path, like the app's developer mode). Use only a digest you released yourself.");
            }
            catch (ArgumentException ex)
            {
                return Fail(stderr, CloudCliExitCode.Usage, $"{ex.Message} Fix the flag it names and run this again.");
            }
        }

        // The only way a VM is made: VmSpec, so the six labels, maxRunDuration and DELETE are checked before a request is built (Hard Rule 10).
        var spec = new VmSpec(
            ProjectId: command.Project,
            InstallationId: command.Install,
            JobId: jobId,
            Model: command.Model,
            AppVersion: command.AppVersion,
            Lifecycle: command.Lifecycle,
            MachineType: command.MachineType,
            MaxRunDuration: command.MaxRunDuration,
            TerminationAction: VmSpec.RequiredTerminationAction)
        {
            ServiceAccountEmail = command.ServiceAccount,
            Metadata = metadata,
        };

        try
        {
            spec.EnsurePreconditions();
        }
        catch (InvalidOperationException ex)
        {
            return Fail(stderr, CloudCliExitCode.Usage, $"{ex.Message} Fix the flag it names and run this again.");
        }

        var vm = await session.Compute.CreateVmAsync(spec, command.Zone, cancellationToken).ConfigureAwait(false);
        if (command.Json)
        {
            stdout.WriteLine(JsonSerializer.Serialize(new { name = vm.Name, zone = vm.Zone, status = vm.Status, maxRunDurationSeconds = (long)command.MaxRunDuration.TotalSeconds }, JsonOptions));
            return CloudCliExitCode.Ok;
        }

        stdout.WriteLine($"OK: created {Ascii(vm.Name)} in {Ascii(vm.Zone)} ({Ascii(vm.Status)}); it deletes itself after {command.MaxRunDuration}. Delete it sooner with: vm delete --project {command.Project} --zone {vm.Zone} --name {vm.Name}");
        return CloudCliExitCode.Ok;
    }

    private static async Task<int> DeleteAsync(CloudCliSession session, VmDeleteCommand command, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        var vm = await session.Compute.GetVmAsync(command.Name, command.Zone, cancellationToken).ConfigureAwait(false);
        if (vm is null)
        {
            // Not "already gone": a wrong zone looks identical, and the VM would keep billing. Look for the name elsewhere.
            var elsewhere = (await session.Compute.ListAppVmsAsync(cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(v => string.Equals(v.Name, command.Name, StringComparison.Ordinal));
            return Fail(stderr, CloudCliExitCode.NotFound, elsewhere is not null
                ? $"{Ascii(command.Name)} is not in zone {Ascii(command.Zone)}; it is in zone {Ascii(elsewhere.Zone)}. Run this again with --zone {Ascii(elsewhere.Zone)}."
                : $"{Ascii(command.Name)} was not found in zone {Ascii(command.Zone)} or any other zone. Check the zone: 'resources list --kind vm' shows where each VM is (nothing listed means it is already deleted).");
        }

        // Never assume a VM is ours: no labels read, or no app label, means leave it alone.
        if (Label(vm.Labels, "app") != VmSpec.AppLabelValue)
        {
            return Fail(stderr, CloudCliExitCode.Refused, $"Refusing to delete '{command.Name}': it does not carry the label app={VmSpec.AppLabelValue}, so it is not one of ours. Delete it in the Google Cloud console if you are sure.");
        }

        await session.Compute.DeleteVmAsync(command.Name, command.Zone, cancellationToken).ConfigureAwait(false);
        stdout.WriteLine(command.Json
            ? JsonSerializer.Serialize(new { name = command.Name, zone = command.Zone, deleted = true }, JsonOptions)
            : $"OK: deleted {Ascii(command.Name)} in {Ascii(command.Zone)}");
        return CloudCliExitCode.Ok;
    }

    private static string ActionFor(CloudErrorKind kind) => kind switch
    {
        CloudErrorKind.Billing => "Turn billing on for the project, then run this again.",
        CloudErrorKind.ApiDisabled => "Enable the Compute Engine API for the project, then run this again.",
        CloudErrorKind.Permission => "Ask the project owner for access, or sign in with the app as another account.",
        CloudErrorKind.OrgPolicy => "Ask the project owner which organization policy blocks this.",
        CloudErrorKind.Quota => "Ask for more quota in the Google Cloud console, or try another region.",
        CloudErrorKind.Stockout => "Try another zone.",
        CloudErrorKind.AlreadyExists => "Pick a different --job, or delete the existing VM first.",
        CloudErrorKind.Network => "Check the internet connection, then run this again.",
        _ => "Run this again, and send the app's diagnostics zip if it keeps failing.",
    };

    private static string? Label(IReadOnlyDictionary<string, string>? labels, string key)
        => labels is not null && labels.TryGetValue(key, out var value) ? value : null;

    private static string? Created(BucketDescriptor bucket)
        => bucket.CreatedAt?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static int Fail(TextWriter stderr, int exitCode, string message)
    {
        // The first line is the error; a usage block (the parser's) follows on its own lines.
        var lines = message.Split('\n');
        stderr.WriteLine("ERROR: " + Ascii(lines[0]));
        foreach (var line in lines.Skip(1))
        {
            stderr.WriteLine(Ascii(line, trim: false));
        }

        return exitCode;
    }

    private static void WriteTable(TextWriter writer, string[] header, IEnumerable<string?[]> rows)
    {
        var cells = rows.Select(r => r.Select(c => string.IsNullOrEmpty(c) ? "-" : Ascii(c)).ToArray()).ToList();
        var widths = header.Select((h, i) => Math.Max(h.Length, cells.Count == 0 ? 0 : cells.Max(r => r[i].Length))).ToArray();
        writer.WriteLine(Format(header, widths));
        foreach (var row in cells)
        {
            writer.WriteLine(Format(row, widths));
        }
    }

    private static string Format(string[] cells, int[] widths)
        => string.Join("  ", cells.Select((c, i) => c.PadRight(widths[i]))).TrimEnd();

    /// <summary>One printable ASCII line: anything else (an API message may carry any character) becomes a question mark and a line break a space.</summary>
    private static string Ascii(string text, bool trim = true)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            builder.Append(c is '\n' or '\r' or '\t' ? ' ' : c is >= ' ' and <= '~' ? c : '?');
        }

        return trim ? builder.ToString().Trim() : builder.ToString().TrimEnd();
    }

    private sealed record ResourceRow(string Kind, string Name, string? Zone, string? Location, string? Status, string? Installation, string? Job);
}
