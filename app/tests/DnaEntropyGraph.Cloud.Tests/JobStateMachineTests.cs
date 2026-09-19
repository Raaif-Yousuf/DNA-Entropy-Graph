using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

public class JobStateMachineTests
{
    public static IEnumerable<object[]> HappyPathTransitions()
    {
        yield return [JobPhase.Draft, JobPhase.Validating];
        yield return [JobPhase.Validating, JobPhase.Uploading];
        yield return [JobPhase.Uploading, JobPhase.Provisioning];
        yield return [JobPhase.Provisioning, JobPhase.Preparing];
        yield return [JobPhase.Preparing, JobPhase.Running];
        yield return [JobPhase.Running, JobPhase.Finalizing];
        yield return [JobPhase.Finalizing, JobPhase.Downloading];
        yield return [JobPhase.Downloading, JobPhase.Completed];
        yield return [JobPhase.Downloading, JobPhase.PartiallyCompleted];
    }

    [Theory]
    [MemberData(nameof(HappyPathTransitions))]
    public void Every_happy_path_step_is_legal(JobPhase from, JobPhase to)
    {
        JobStateMachine.IsLegalTransition(from, to).ShouldBeTrue();
    }

    [Theory]
    [InlineData(JobPhase.Validating, JobPhase.Cancelling)]
    [InlineData(JobPhase.Provisioning, JobPhase.Cancelling)]
    [InlineData(JobPhase.Running, JobPhase.Cancelling)]
    [InlineData(JobPhase.Cancelling, JobPhase.Cancelled)]
    public void Cancelling_is_legal_from_every_active_phase(JobPhase from, JobPhase to)
    {
        JobStateMachine.IsLegalTransition(from, to).ShouldBeTrue();
    }

    [Theory]
    [InlineData(JobPhase.Draft, JobPhase.Running)]
    [InlineData(JobPhase.Completed, JobPhase.Running)]
    [InlineData(JobPhase.Running, JobPhase.Draft)]
    [InlineData(JobPhase.Downloading, JobPhase.Uploading)]
    public void A_skipped_or_backward_transition_is_illegal(JobPhase from, JobPhase to)
    {
        JobStateMachine.IsLegalTransition(from, to).ShouldBeFalse();
    }

    [Fact]
    public void EnsureLegalTransition_throws_naming_both_phases_for_an_illegal_move()
    {
        var ex = Should.Throw<InvalidOperationException>(() => JobStateMachine.EnsureLegalTransition(JobPhase.Draft, JobPhase.Completed));

        ex.Message.ShouldContain("Draft");
        ex.Message.ShouldContain("Completed");
    }

    [Theory]
    [InlineData(JobPhase.Completed)]
    [InlineData(JobPhase.PartiallyCompleted)]
    [InlineData(JobPhase.Cancelled)]
    [InlineData(JobPhase.Failed)]
    public void The_four_terminal_phases_have_no_legal_next_phase(JobPhase terminal)
    {
        JobStateMachine.IsTerminal(terminal).ShouldBeTrue();

        foreach (JobPhase candidate in Enum.GetValues<JobPhase>())
        {
            JobStateMachine.IsLegalTransition(terminal, candidate).ShouldBeFalse();
        }
    }

    [Fact]
    public void A_non_terminal_phase_is_not_reported_as_terminal()
    {
        JobStateMachine.IsTerminal(JobPhase.Running).ShouldBeFalse();
    }

    [Fact]
    public void HasAlreadyPassed_is_true_only_for_a_happy_path_phase_strictly_earlier_than_current()
    {
        JobStateMachine.HasAlreadyPassed(JobPhase.Provisioning, JobPhase.Validating).ShouldBeTrue();
        JobStateMachine.HasAlreadyPassed(JobPhase.Provisioning, JobPhase.Provisioning).ShouldBeFalse();
        JobStateMachine.HasAlreadyPassed(JobPhase.Provisioning, JobPhase.Running).ShouldBeFalse();
    }

    [Fact]
    public void HasAlreadyPassed_is_false_for_a_phase_outside_the_happy_path_order()
    {
        // Cancelling/Cancelled/Failed are not in HappyPathOrder at all -
        // this must not throw or silently treat them as "index 0".
        JobStateMachine.HasAlreadyPassed(JobPhase.Cancelled, JobPhase.Validating).ShouldBeFalse();
        JobStateMachine.HasAlreadyPassed(JobPhase.Running, JobPhase.Failed).ShouldBeFalse();
    }
}
