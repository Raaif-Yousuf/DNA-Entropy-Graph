using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.LocalEngine;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.LocalEngine.Tests;

public sealed class RunTargetResolverTests
{
    private static LocalReadiness Ready(bool cuda = true, bool eligible = true)
        => new(
            new LocalCapability(eligible, eligible ? [] : [LocalBlocker.NoNvidiaGpu], null, 100L << 30),
            new LocalEngineHealth(LocalEngineState.Healthy, "0.0.1", cuda, cuda, string.Empty));

    private static LocalReadiness NotInstalled()
        => new(new LocalCapability(true, [], null, 100L << 30), LocalEngineHealth.NotInstalled());

    private static (RunTargetResolver Resolver, ILocalReadinessSource Source) Make(LocalReadiness readiness)
    {
        var source = Substitute.For<ILocalReadinessSource>();
        source.CheckAsync(Arg.Any<CancellationToken>()).Returns(readiness);
        return (new RunTargetResolver(source), source);
    }

    private static RunOptions Options(string target, string model = "evo2_7b", PredictorSelection predictor = PredictorSelection.Evo2)
        => new() { ModelId = model, RunTarget = target, Predictor = predictor };

    [Fact]
    public async Task An_explicit_Cloud_stays_Cloud_without_looking_at_the_PC()
    {
        var (resolver, source) = Make(Ready());

        (await resolver.ResolveAsync(Options("Cloud"), CancellationToken.None)).Target.ShouldBe(RunTargets.Cloud);
        await source.DidNotReceive().CheckAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_explicit_This_PC_stays_This_PC_even_when_the_PC_is_not_ready_so_a_failure_is_visible_not_a_silent_paid_cloud_run()
    {
        var (resolver, _) = Make(NotInstalled());

        (await resolver.ResolveAsync(Options("This PC"), CancellationToken.None)).Target.ShouldBe(RunTargets.ThisPc);
    }

    [Fact]
    public async Task Auto_picks_This_PC_when_the_engine_is_healthy_with_cuda_and_the_model_fits()
    {
        var (resolver, _) = Make(Ready());

        var decision = await resolver.ResolveAsync(Options("Auto"), CancellationToken.None);

        decision.Target.ShouldBe(RunTargets.ThisPc);
        decision.AutoReason.ShouldBe("local_ready");
    }

    [Fact]
    public async Task Auto_picks_Cloud_when_the_engine_is_not_installed()
    {
        var (resolver, _) = Make(NotInstalled());

        var decision = await resolver.ResolveAsync(Options("Auto"), CancellationToken.None);

        decision.Target.ShouldBe(RunTargets.Cloud);
        decision.AutoReason.ShouldBe("local_not_ready");
    }

    [Fact]
    public async Task Auto_picks_Cloud_when_the_GPU_is_too_small()
    {
        var (resolver, _) = Make(Ready(eligible: false));

        var decision = await resolver.ResolveAsync(Options("Auto"), CancellationToken.None);

        decision.Target.ShouldBe(RunTargets.Cloud);
        decision.AutoReason.ShouldBe("local_blocked");
    }

    [Fact]
    public async Task Auto_picks_Cloud_when_torch_sees_no_cuda_in_an_otherwise_eligible_PC()
    {
        var (resolver, _) = Make(Ready(cuda: false));

        (await resolver.ResolveAsync(Options("Auto"), CancellationToken.None)).Target.ShouldBe(RunTargets.Cloud);
    }

    [Theory]
    [InlineData("evo2_20b")]
    [InlineData("evo2_40b")]
    [InlineData("evo2_1b_base")]
    public async Task Auto_picks_Cloud_for_a_model_that_needs_Hopper_FP8_even_on_a_ready_PC(string model)
    {
        var (resolver, _) = Make(Ready());

        var decision = await resolver.ResolveAsync(Options("Auto", model), CancellationToken.None);

        decision.Target.ShouldBe(RunTargets.Cloud);
        decision.AutoReason.ShouldBe("model_needs_fp8");
    }

    [Fact]
    public async Task Auto_with_the_mock_predictor_runs_here_when_the_engine_is_healthy_even_with_no_GPU()
    {
        var (resolver, _) = Make(Ready(cuda: false, eligible: false));

        var decision = await resolver.ResolveAsync(Options("Auto", predictor: PredictorSelection.MockTestPredictor), CancellationToken.None);

        decision.Target.ShouldBe(RunTargets.ThisPc);
    }

    [Fact]
    public async Task Auto_matches_regardless_of_case()
    {
        var (resolver, _) = Make(Ready());

        (await resolver.ResolveAsync(Options("auto"), CancellationToken.None)).Target.ShouldBe(RunTargets.ThisPc);
    }

    [Fact]
    public async Task An_unknown_target_is_handed_back_unchanged_so_the_run_refuses_it()
    {
        var (resolver, _) = Make(Ready());

        (await resolver.ResolveAsync(Options("The Moon"), CancellationToken.None)).Target.ShouldBe("The Moon");
    }

    [Fact]
    public async Task A_readiness_check_that_throws_means_Cloud_for_Auto()
    {
        var source = Substitute.For<ILocalReadinessSource>();
        source.CheckAsync(Arg.Any<CancellationToken>()).Returns<LocalReadiness>(_ => throw new InvalidOperationException("boom"));

        var decision = await new RunTargetResolver(source).ResolveAsync(Options("Auto"), CancellationToken.None);

        decision.Target.ShouldBe(RunTargets.Cloud);
    }
}
