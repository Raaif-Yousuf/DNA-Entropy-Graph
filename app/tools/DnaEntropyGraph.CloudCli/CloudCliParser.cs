using System.Globalization;
using System.Text.RegularExpressions;

namespace DnaEntropyGraph.CloudCli;

public enum ResourceKind
{
    All,
    Vm,
    Bucket,
}

/// <summary>One parsed command line. Every verb names its project: there is no default project (nothing guesses where to spend or delete).</summary>
public abstract record CliCommand(string Project, bool Json);

public sealed record ResourcesListCommand(string Project, string? Install, ResourceKind Kind, bool Json) : CliCommand(Project, Json);

public sealed record BucketDescribeCommand(string Project, string? Name, bool Json) : CliCommand(Project, Json);

/// <summary>
/// <paramref name="Job"/> null means "generate one that follows the job id convention". There is no termination-action field: it is always
/// DELETE (Hard Rule 10), and <see cref="CloudCliParser"/> refuses a flag that asks for anything else.
/// </summary>
public sealed record VmCreateCommand(
    string Project,
    string Zone,
    string Install,
    string? Job,
    string Model,
    string MachineType,
    string ServiceAccount,
    string AppVersion,
    string Lifecycle,
    TimeSpan MaxRunDuration,
    bool Json) : CliCommand(Project, Json);

public sealed record VmDeleteCommand(string Project, string Zone, string Name, bool Json) : CliCommand(Project, Json);

/// <summary>Either a <see cref="Command"/> or an <see cref="Error"/> naming what to fix (never both).</summary>
public sealed record CliParseResult(CliCommand? Command, string? Error);

/// <summary>
/// Hand-written parser for the CloudCli command line (issue #60): <c>--flag value</c> or <c>--flag=value</c>, plus the one boolean
/// flag <c>--json</c>. A bad line is an error before any session or network call exists.
/// </summary>
public static class CloudCliParser
{
    public const string DefaultModel = "mock";
    public const string DefaultMachineType = "g2-standard-8";
    public const string DefaultLifecycle = "test";
    public const string DefaultAppVersion = "cli";
    public static readonly TimeSpan DefaultMaxRunDuration = TimeSpan.FromMinutes(30);

    public const string Usage =
        "Usage:\n"
        + "  resources list --project <id> [--install <id>] [--kind vm|bucket|all] [--json]\n"
        + "  bucket describe --project <id> [--name <bucket>] [--json]\n"
        + "  vm create --project <id> --zone <zone> --install <id> --service-account <email> [--job <id>] [--model <m>]\n"
        + "            [--machine-type <t>] [--lifecycle <l>] [--app-version <v>] [--max-run-duration 90|90s|30m|2h] [--json]\n"
        + "  vm delete --project <id> --zone <zone> --name <deg-...> [--json]";

    private static readonly Regex DurationPattern = new(@"^(?<n>\d{1,9})(?<unit>[smh]?)$", RegexOptions.Compiled);

    private static readonly string[] ResourcesListFlags = ["project", "install", "kind"];
    private static readonly string[] BucketDescribeFlags = ["project", "name"];
    private static readonly string[] VmCreateFlags =
        ["project", "zone", "install", "job", "model", "machine-type", "service-account", "app-version", "lifecycle", "max-run-duration", "instance-termination-action"];
    private static readonly string[] VmDeleteFlags = ["project", "zone", "name"];

    public static CliParseResult Parse(IReadOnlyList<string> args)
    {
        if (args.Count < 2)
        {
            return Fail($"Expected a command. {Usage}");
        }

        var (noun, verb) = (args[0], args[1]);
        string[]? allowed = (noun, verb) switch
        {
            ("resources", "list") => ResourcesListFlags,
            ("bucket", "describe") => BucketDescribeFlags,
            ("vm", "create") => VmCreateFlags,
            ("vm", "delete") => VmDeleteFlags,
            _ => null,
        };
        if (allowed is null)
        {
            return Fail($"Unknown command '{noun} {verb}'. {Usage}");
        }

        var read = ReadFlags(args.Skip(2).ToList(), allowed);
        if (read.Error is not null)
        {
            return Fail(read.Error);
        }

        var flags = read.Flags;
        var json = read.Json;
        string? Value(string name) => flags.TryGetValue(name, out var value) ? value : null;

        var project = Value("project");
        if (string.IsNullOrWhiteSpace(project))
        {
            return Fail("Missing --project <id>: say which Google Cloud project to look at.");
        }

        return (noun, verb) switch
        {
            ("resources", "list") => ParseResourcesList(project, Value("install"), Value("kind"), json),
            ("bucket", "describe") => Ok(new BucketDescribeCommand(project, Value("name"), json)),
            ("vm", "create") => ParseVmCreate(project, Value, json),
            _ => ParseVmDelete(project, Value, json),
        };
    }

    /// <summary>Seconds (<c>90</c>, <c>90s</c>), minutes (<c>30m</c>) or hours (<c>2h</c>); zero, negative and anything else is refused.</summary>
    public static bool TryParseDuration(string? text, out TimeSpan duration)
    {
        duration = default;
        var match = DurationPattern.Match(text ?? string.Empty);
        if (!match.Success)
        {
            return false;
        }

        var amount = long.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture);
        if (amount <= 0)
        {
            return false;
        }

        duration = match.Groups["unit"].Value switch
        {
            "m" => TimeSpan.FromMinutes(amount),
            "h" => TimeSpan.FromHours(amount),
            _ => TimeSpan.FromSeconds(amount),
        };
        return true;
    }

    private static CliParseResult ParseResourcesList(string project, string? install, string? kind, bool json)
    {
        var parsedKind = (kind ?? "all").ToLowerInvariant() switch
        {
            "all" => ResourceKind.All,
            "vm" => ResourceKind.Vm,
            "bucket" => ResourceKind.Bucket,
            _ => (ResourceKind?)null,
        };
        return parsedKind is { } k
            ? Ok(new ResourcesListCommand(project, install, k, json))
            : Fail("--kind must be vm, bucket or all.");
    }

    private static CliParseResult ParseVmCreate(string project, Func<string, string?> value, bool json)
    {
        var termination = value("instance-termination-action");
        if (termination is not null && !string.Equals(termination, "DELETE", StringComparison.Ordinal))
        {
            return Fail("--instance-termination-action can only be DELETE: every VM this tool creates deletes itself (Hard Rule 10).");
        }

        var zone = value("zone");
        if (string.IsNullOrWhiteSpace(zone))
        {
            return Fail("Missing --zone <zone>, for example us-central1-a.");
        }

        var install = value("install");
        if (string.IsNullOrWhiteSpace(install))
        {
            return Fail("Missing --install <installation id>: every VM carries one so the Cloud page can find it.");
        }

        var serviceAccount = value("service-account");
        if (string.IsNullOrWhiteSpace(serviceAccount))
        {
            return Fail("Missing --service-account <email>: the VM must run as a service account that can reach its bucket.");
        }

        var duration = DefaultMaxRunDuration;
        if (value("max-run-duration") is { } text && !TryParseDuration(text, out duration))
        {
            return Fail("--max-run-duration must be a positive number of seconds, or a number ending in s, m or h (for example 90, 30m, 2h).");
        }

        return Ok(new VmCreateCommand(
            project, zone, install, value("job"), value("model") ?? DefaultModel, value("machine-type") ?? DefaultMachineType, serviceAccount,
            value("app-version") ?? DefaultAppVersion, value("lifecycle") ?? DefaultLifecycle, duration, json));
    }

    private static CliParseResult ParseVmDelete(string project, Func<string, string?> value, bool json)
    {
        var zone = value("zone");
        if (string.IsNullOrWhiteSpace(zone))
        {
            return Fail("Missing --zone <zone>, for example us-central1-a.");
        }

        var name = value("name");
        return string.IsNullOrWhiteSpace(name)
            ? Fail("Missing --name <deg-...>: the VM to delete.")
            : Ok(new VmDeleteCommand(project, zone, name, json));
    }

    private static (Dictionary<string, string> Flags, bool Json, string? Error) ReadFlags(IReadOnlyList<string> tokens, string[] allowed)
    {
        var flags = new Dictionary<string, string>(StringComparer.Ordinal);
        var json = false;
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                return (flags, json, $"Unexpected '{token}': every value follows a flag such as --project.");
            }

            var body = token[2..];
            string? inline = null;
            var equals = body.IndexOf('=', StringComparison.Ordinal);
            if (equals >= 0)
            {
                (body, inline) = (body[..equals], body[(equals + 1)..]);
            }

            if (body == "json")
            {
                if (inline is null || inline.Equals("true", StringComparison.OrdinalIgnoreCase))
                {
                    json = true;
                }
                else if (inline.Equals("false", StringComparison.OrdinalIgnoreCase))
                {
                    json = false;
                }
                else
                {
                    return (flags, json, $"--json takes true or false (or no value), not '{inline}'.");
                }

                continue;
            }

            if (!allowed.Contains(body))
            {
                return (flags, json, $"Unknown option --{body} for this command.");
            }

            if (inline is null)
            {
                if (i + 1 >= tokens.Count || tokens[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    return (flags, json, $"--{body} needs a value.");
                }

                inline = tokens[++i];
            }

            flags[body] = inline;
        }

        return (flags, json, null);
    }

    private static CliParseResult Ok(CliCommand command) => new(command, null);

    private static CliParseResult Fail(string error) => new(null, error);
}
