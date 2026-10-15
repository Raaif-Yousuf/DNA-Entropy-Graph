using DnaEntropyGraph.CloudCli;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests.CloudCli;

/// <summary>Issue #60: the hand-written argument parser. A bad line is a usage error before any network call exists.</summary>
public class CloudCliParserTests
{
    private static CliCommand Ok(params string[] args)
    {
        var result = CloudCliParser.Parse(args);
        result.Error.ShouldBeNull();
        return result.Command.ShouldNotBeNull();
    }

    [Fact]
    public void Resources_list_reads_project_install_kind_and_json()
    {
        var command = Ok("resources", "list", "--project", "my-lab", "--install", "inst-1", "--kind", "vm", "--json").ShouldBeOfType<ResourcesListCommand>();

        command.Project.ShouldBe("my-lab");
        command.Install.ShouldBe("inst-1");
        command.Kind.ShouldBe(ResourceKind.Vm);
        command.Json.ShouldBeTrue();
    }

    [Fact]
    public void Resources_list_defaults_to_every_kind_and_a_table()
    {
        var command = Ok("resources", "list", "--project", "my-lab").ShouldBeOfType<ResourcesListCommand>();

        command.Install.ShouldBeNull();
        command.Kind.ShouldBe(ResourceKind.All);
        command.Json.ShouldBeFalse();
    }

    [Theory]
    [InlineData("--json=true", true)]
    [InlineData("--json=false", false)]
    [InlineData("--json=TRUE", true)]
    public void Json_accepts_an_explicit_true_or_false(string flag, bool expected)
    {
        var command = Ok("resources", "list", "--project", "my-lab", flag).ShouldBeOfType<ResourcesListCommand>();

        command.Json.ShouldBe(expected);
    }

    [Fact]
    public void Json_with_any_other_value_is_a_clear_usage_error()
    {
        var result = CloudCliParser.Parse(["resources", "list", "--project", "my-lab", "--json=maybe"]);

        result.Command.ShouldBeNull();
        result.Error.ShouldNotBeNull().ShouldContain("--json");
        result.Error.ShouldContain("true or false");
    }

    [Fact]
    public void Flags_accept_the_equals_form()
    {
        Ok("resources", "list", "--project=my-lab", "--install=inst-1").ShouldBeOfType<ResourcesListCommand>().Install.ShouldBe("inst-1");
    }

    [Fact]
    public void Bucket_describe_reads_the_optional_name()
    {
        var command = Ok("bucket", "describe", "--project", "my-lab", "--name", "deg-1-abc").ShouldBeOfType<BucketDescribeCommand>();

        command.Name.ShouldBe("deg-1-abc");
        Ok("bucket", "describe", "--project", "my-lab").ShouldBeOfType<BucketDescribeCommand>().Name.ShouldBeNull();
    }

    [Fact]
    public void Vm_create_reads_every_field_and_a_duration_in_minutes()
    {
        var command = Ok(
            "vm", "create", "--project", "my-lab", "--zone", "us-central1-a", "--install", "inst-1", "--job", "job1", "--model", "evo2_7b",
            "--machine-type", "g2-standard-8", "--service-account", "w@my-lab.iam.gserviceaccount.com", "--max-run-duration", "45m",
            "--lifecycle", "test", "--app-version", "0.1.0").ShouldBeOfType<VmCreateCommand>();

        command.Zone.ShouldBe("us-central1-a");
        command.Install.ShouldBe("inst-1");
        command.Job.ShouldBe("job1");
        command.ServiceAccount.ShouldBe("w@my-lab.iam.gserviceaccount.com");
        command.MaxRunDuration.ShouldBe(TimeSpan.FromMinutes(45));
        command.Lifecycle.ShouldBe("test");
    }

    [Fact]
    public void Vm_create_never_has_a_flag_to_weaken_the_termination_action()
    {
        var weaken = CloudCliParser.Parse(["vm", "create", "--project", "p", "--zone", "z", "--install", "i", "--service-account", "w@p.iam.gserviceaccount.com", "--instance-termination-action", "STOP"]);
        weaken.Error.ShouldNotBeNull();
        weaken.Error.ShouldContain("DELETE");

        Ok("vm", "create", "--project", "p", "--zone", "z", "--install", "i", "--service-account", "w@p.iam.gserviceaccount.com", "--instance-termination-action", "DELETE")
            .ShouldBeOfType<VmCreateCommand>();
    }

    [Fact]
    public void Vm_delete_reads_zone_and_name()
    {
        var command = Ok("vm", "delete", "--project", "my-lab", "--zone", "us-central1-a", "--name", "deg-job1").ShouldBeOfType<VmDeleteCommand>();

        command.Zone.ShouldBe("us-central1-a");
        command.Name.ShouldBe("deg-job1");
    }

    public static TheoryData<string[], string> BadLines => new()
    {
        { [], "Usage" },
        { ["resources"], "Usage" },
        { ["frobnicate", "list"], "Usage" },
        { ["resources", "list"], "--project" },
        { ["resources", "list", "--project"], "--project" },
        { ["resources", "list", "--project", "p", "--kind", "disk"], "--kind" },
        { ["resources", "list", "--project", "p", "--bogus", "x"], "--bogus" },
        { ["bucket", "describe"], "--project" },
        { ["vm", "create", "--project", "p"], "--zone" },
        { ["vm", "create", "--project", "p", "--zone", "z", "--install", "i"], "--service-account" },
        { ["vm", "create", "--project", "p", "--zone", "z", "--install", "i", "--service-account", "e", "--max-run-duration", "soon"], "--max-run-duration" },
        { ["vm", "delete", "--project", "p", "--zone", "z"], "--name" },
    };

    [Theory]
    [MemberData(nameof(BadLines))]
    public void A_bad_line_is_a_usage_error_naming_the_flag_to_fix(string[] args, string mentions)
    {
        var result = CloudCliParser.Parse(args);

        result.Command.ShouldBeNull();
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain(mentions);
    }

    [Fact]
    public void The_bad_line_table_is_not_vacuous()
    {
        BadLines.Count().ShouldBeGreaterThanOrEqualTo(10);
    }

    [Theory]
    [InlineData("90", 90)]
    [InlineData("90s", 90)]
    [InlineData("30m", 1800)]
    [InlineData("2h", 7200)]
    public void Durations_parse_as_seconds_minutes_or_hours(string text, int seconds)
    {
        CloudCliParser.TryParseDuration(text, out var parsed).ShouldBeTrue();
        parsed.ShouldBe(TimeSpan.FromSeconds(seconds));
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-5m")]
    [InlineData("1d")]
    public void A_zero_negative_or_unknown_duration_is_refused(string text)
    {
        CloudCliParser.TryParseDuration(text, out _).ShouldBeFalse();
    }
}
