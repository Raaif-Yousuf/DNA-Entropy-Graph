using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>Issue #55: VmSpec rejects a spec missing a label, a maxRunDuration, or a value the real Compute API would itself reject - before any request is built.</summary>
public class VmSpecTests
{
    private static VmSpec ValidSpec() => new(
        ProjectId: "my-project",
        InstallationId: "install-1",
        JobId: "20260919-084512-ab23cd",
        Model: "evo2_7b",
        AppVersion: "0.1.0",
        Lifecycle: "run",
        MachineType: "g2-standard-8",
        MaxRunDuration: TimeSpan.FromHours(4),
        TerminationAction: "DELETE");

    public static IEnumerable<object[]> RequiredFieldMutations()
    {
        yield return new object[] { (Func<VmSpec, VmSpec>)(s => s with { ProjectId = "" }), "project-id" };
        yield return new object[] { (Func<VmSpec, VmSpec>)(s => s with { InstallationId = "" }), "installation-id" };
        yield return new object[] { (Func<VmSpec, VmSpec>)(s => s with { JobId = "" }), "job-id" };
        yield return new object[] { (Func<VmSpec, VmSpec>)(s => s with { Model = "" }), "model" };
        yield return new object[] { (Func<VmSpec, VmSpec>)(s => s with { AppVersion = "" }), "app-version" };
        yield return new object[] { (Func<VmSpec, VmSpec>)(s => s with { Lifecycle = "" }), "lifecycle" };
        yield return new object[] { (Func<VmSpec, VmSpec>)(s => s with { TerminationAction = "" }), "instanceTerminationAction" };
    }

    [Theory]
    [MemberData(nameof(RequiredFieldMutations))]
    public void Removing_one_required_field_makes_EnsurePreconditions_throw_with_the_fields_name(Func<VmSpec, VmSpec> mutate, string expectedFieldNameInMessage)
    {
        var spec = mutate(ValidSpec());

        var ex = Should.Throw<InvalidOperationException>(() => spec.EnsurePreconditions());

        ex.Message.ShouldContain(expectedFieldNameInMessage);
    }

    [Fact]
    public void A_zero_maxRunDuration_throws()
    {
        var spec = ValidSpec() with { MaxRunDuration = TimeSpan.Zero };

        Should.Throw<InvalidOperationException>(() => spec.EnsurePreconditions())
            .Message.ShouldContain("maxRunDuration");
    }

    [Fact]
    public void A_negative_maxRunDuration_throws()
    {
        var spec = ValidSpec() with { MaxRunDuration = TimeSpan.FromHours(-1) };

        Should.Throw<InvalidOperationException>(() => spec.EnsurePreconditions());
    }

    [Fact]
    public void VmName_is_deg_prefixed_job_id()
    {
        var spec = ValidSpec();

        spec.VmName.ShouldBe("deg-20260919-084512-ab23cd");
    }

    [Fact]
    public void A_job_id_that_would_produce_an_invalid_Compute_resource_name_is_rejected_before_any_request_is_built()
    {
        // An underscore is a legal Compute Engine LABEL character but not a
        // legal character inside a Compute Engine resource NAME - so
        // "job_1" passes the label-value check yet must still be rejected
        // once it is used to build the VM name "deg-job_1". This is
        // exactly the "a spec that passes our guard and is rejected by the
        // API is a worse failure" case the lane brief asks to check for.
        var spec = ValidSpec() with { JobId = "job_1" };

        var ex = Should.Throw<InvalidOperationException>(() => spec.EnsurePreconditions());

        ex.Message.ShouldContain("deg-job_1");
    }

    [Fact]
    public void A_label_value_with_a_dot_is_rejected_by_EnsurePreconditions_even_though_it_is_a_common_looking_value()
    {
        // "job-1.5" reads like a perfectly normal identifier but a dot is
        // not in Compute Engine's label charset - this is the exact class
        // of spec that would pass a naive non-empty check and then be
        // rejected by the real API.
        var spec = ValidSpec() with { JobId = "job-1.5" };

        Should.Throw<InvalidOperationException>(() => spec.EnsurePreconditions())
            .Message.ShouldContain("job-id");
    }

    [Fact]
    public void ToLabels_returns_exactly_the_six_standard_labels()
    {
        var labels = ValidSpec().ToLabels();

        labels.Keys.ShouldBe(["app", "installation-id", "job-id", "model", "app-version", "lifecycle"], ignoreOrder: true);
        labels["app"].ShouldBe(VmSpec.AppLabelValue);
    }

    [Fact]
    public void ToLabels_sanitizes_a_dotted_semantic_version_into_a_valid_label_value()
    {
        // AppVersion is the one field VmSpec expects to arrive looking like
        // "0.1.0" - a real semantic version - and sanitizes for the label
        // rather than rejecting, since rejecting every normal app version
        // would make VmSpec unusable from the app's own assembly version.
        var labels = ValidSpec().ToLabels();

        labels["app-version"].ShouldBe("0-1-0");
    }

    [Fact]
    public void ToLabels_calls_EnsurePreconditions_first_so_a_missing_label_never_reaches_label_construction()
    {
        var spec = ValidSpec() with { Model = "" };

        Should.Throw<InvalidOperationException>(() => spec.ToLabels())
            .Message.ShouldContain("model");
    }

    [Fact]
    public void An_app_version_made_entirely_of_disallowed_characters_still_produces_a_valid_label_by_1_to_1_substitution()
    {
        // A 1:1 character replacement can never turn a non-empty string
        // into an empty one, so this never throws - unlike RequireLabel's
        // five other fields, which are validated as-is, never rewritten.
        var spec = ValidSpec() with { AppVersion = "..." };

        var labels = spec.ToLabels();

        labels["app-version"].ShouldBe("---");
    }

    [Fact]
    public void An_app_version_longer_than_63_characters_is_truncated_to_a_valid_label_length()
    {
        var spec = ValidSpec() with { AppVersion = new string('9', 100) };

        var labels = spec.ToLabels();

        labels["app-version"].Length.ShouldBe(63);
    }
}

public class JobIdTests
{
    [Fact]
    public void NewId_follows_the_yyyymmdd_hhmmss_base32_convention()
    {
        var id = DnaEntropyGraph.Core.Cloud.JobId.NewId(new DateTimeOffset(2026, 9, 19, 8, 45, 12, TimeSpan.Zero), new Random(42));

        DnaEntropyGraph.Core.Cloud.JobId.MatchesConvention(id).ShouldBeTrue();
        id.ShouldStartWith("20260919-084512-");
        id.Length.ShouldBe("20260919-084512-".Length + 6);
    }

    [Fact]
    public void NewId_is_reproducible_for_a_seeded_random_so_a_test_can_assert_an_exact_value()
    {
        var a = DnaEntropyGraph.Core.Cloud.JobId.NewId(new DateTimeOffset(2026, 9, 19, 8, 45, 12, TimeSpan.Zero), new Random(1));
        var b = DnaEntropyGraph.Core.Cloud.JobId.NewId(new DateTimeOffset(2026, 9, 19, 8, 45, 12, TimeSpan.Zero), new Random(1));

        a.ShouldBe(b);
    }

    [Theory]
    [InlineData("job-1", false)]
    [InlineData("20260919-084512-ABCDEF", false)]
    [InlineData("20260919-084512-ab23cd", true)]
    public void MatchesConvention_is_strict_about_the_exact_shape(string candidate, bool expected)
    {
        DnaEntropyGraph.Core.Cloud.JobId.MatchesConvention(candidate).ShouldBe(expected);
    }
}
