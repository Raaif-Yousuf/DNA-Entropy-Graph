using DnaEntropyGraph.Cloud;
using DnaEntropyGraph.Cloud.Rest;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>
/// Issue #92: "wait for a GPU" (Compute FLEX_START). The option reaches the spec, the spec reaches the instance body
/// (provisioningModel=FLEX_START, a wait for capacity, DELETE and the run limit kept), a queued create that is granted completes the run,
/// one that expires ends as a stockout, the run says it is waiting, and a cancel while queued removes the pending request.
/// </summary>
public class FlexStartProvisioningTests
{
    private const string Project = "my-project";
    private const string Zone = "us-central1-a";

    private static VmSpec Spec(string jobId, bool flex, bool spot = false, TimeSpan? wait = null) => new(
        ProjectId: Project,
        InstallationId: "install-1",
        JobId: jobId,
        Model: "evo2_7b",
        AppVersion: "0.1.0",
        Lifecycle: "stop",
        MachineType: "g2-standard-8",
        MaxRunDuration: TimeSpan.FromHours(4),
        TerminationAction: "DELETE")
    {
        FlexStart = flex,
        FlexStartWait = flex ? wait ?? TimeSpan.FromHours(2) : null,
        Spot = spot,
    };

    private static CloudJobRunner Runner(FakeGcp gcp, InMemoryRunRepository repo, Action<RunNotice>? onNotice = null)
        => new(gcp, gcp, gcp, gcp, gcp, repo, null)
        {
            ResultPollInterval = TimeSpan.FromMilliseconds(1),
            ResultTimeout = TimeSpan.FromSeconds(5),
            CallTimeout = TimeSpan.FromMilliseconds(500),
            EnsureBucketTimeout = TimeSpan.FromMilliseconds(500),
            CreateTimeout = TimeSpan.FromMilliseconds(100),
            CreateSettleTimeout = TimeSpan.FromSeconds(2),
            UploadTimeout = TimeSpan.FromSeconds(2),
            BootTimeout = TimeSpan.FromSeconds(2),
            LifecycleTimeout = TimeSpan.FromSeconds(2),
            LifecyclePollInterval = TimeSpan.FromMilliseconds(1),
            OnNotice = onNotice,
        };

    private static RunRecord LastRow(InMemoryRunRepository repo, string jobId) => repo.AllRecordedInOrder.Last(r => r.JobId == jobId);

    [Fact]
    public void The_wait_option_reaches_the_VmSpec_and_is_off_by_default()
    {
        var inputs = new[] { new DnaEntropyGraph.Core.Contract.StagedInput(@"C:\x\a.gb", "a.gb") };
        var on = CloudJobRequestFactory.Create(new RunOptions { ModelId = "evo2_7b", RunTarget = "Cloud", WaitForGpu = true }, "20260101-000000-abcdef", new CloudScope(Project), "install-1", "0.1.0", null, inputs, @"C:\out");
        var off = CloudJobRequestFactory.Create(new RunOptions { ModelId = "evo2_7b", RunTarget = "Cloud" }, "20260101-000000-abcdef", new CloudScope(Project), "install-1", "0.1.0", null, inputs, @"C:\out");

        on.Spec.FlexStart.ShouldBeTrue();
        on.Spec.FlexStartWait.ShouldBe(TimeSpan.FromMinutes(new RunOptions { ModelId = "x", RunTarget = "Cloud" }.WaitForGpuMinutes));
        on.Spec.TerminationAction.ShouldBe("DELETE");
        off.Spec.FlexStart.ShouldBeFalse();
        off.Spec.FlexStartWait.ShouldBeNull();
    }

    [Fact]
    public void A_flex_start_body_says_FLEX_START_with_a_wait_for_capacity_and_keeps_DELETE_and_the_run_limit()
    {
        var spec = Spec("job-f1", flex: true, wait: TimeSpan.FromHours(3));

        var body = ComputeVmShape.Build(spec, Zone, spec.ToLabels(), "sa@my-project.iam.gserviceaccount.com");

        body.Scheduling.ProvisioningModel.ShouldBe("FLEX_START");
        body.Scheduling.InstanceTerminationAction.ShouldBe("DELETE");
        body.Scheduling.MaxRunDuration.Seconds.ShouldBe((long)TimeSpan.FromHours(4).TotalSeconds);
        body.Params.ShouldNotBeNull();
        body.Params!.RequestValidForDuration.Seconds.ShouldBe((long)TimeSpan.FromHours(3).TotalSeconds);
    }

    [Fact]
    public void An_on_demand_body_has_no_wait_for_capacity()
    {
        var spec = Spec("job-f2", flex: false);

        var body = ComputeVmShape.Build(spec, Zone, spec.ToLabels(), "sa@my-project.iam.gserviceaccount.com");

        body.Scheduling.ProvisioningModel.ShouldBeNull();
        body.Params.ShouldBeNull();
    }

    [Fact]
    public void A_flex_start_spec_that_is_also_Spot_or_has_no_wait_or_is_too_long_is_refused_before_any_request()
    {
        Should.Throw<InvalidOperationException>(() => Spec("job-f3", flex: true, spot: true).EnsurePreconditions());
        Should.Throw<InvalidOperationException>(() => (Spec("job-f4", flex: true) with { MaxRunDuration = TimeSpan.FromDays(8) }).EnsurePreconditions());
        Should.Throw<InvalidOperationException>(() => Spec("job-f5", flex: true, wait: TimeSpan.FromDays(8)).EnsurePreconditions());
        Should.Throw<InvalidOperationException>(() => (Spec("job-f6", flex: true) with { FlexStartWait = null }).EnsurePreconditions());
        Should.NotThrow(() => Spec("job-f7", flex: true).EnsurePreconditions());
    }

    [Fact]
    public async Task After_a_stockout_choosing_wait_recreates_as_FLEX_START_and_the_run_completes_when_the_fake_grants_capacity()
    {
        var gcp = new FakeGcp().WithZoneStockout(Zone, times: 1).WithFlexStartGrantedAfter(TimeSpan.FromMilliseconds(400));
        var repo = new InMemoryRunRepository();

        var first = await Runner(gcp, repo).RunAsync(TestInputs.Request("job-f8", Spec("job-f8", flex: false)), CancellationToken.None);
        first.FinalPhase.ShouldBe(JobPhase.Failed);
        first.FailureKind.ShouldBe(CloudErrorKind.Stockout);

        var second = await Runner(gcp, repo).RunAsync(TestInputs.Request("job-f9", Spec("job-f9", flex: true)), CancellationToken.None);

        second.FinalPhase.ShouldBe(JobPhase.Completed, second.FailureMessage);
        gcp.CreatedSpecs.Select(s => s.FlexStart).ShouldBe([false, true]);
        gcp.CreatedSpecs.Last().TerminationAction.ShouldBe("DELETE");
        gcp.CreatedSpecs.Last().FlexStartWait.ShouldBe(TimeSpan.FromHours(2));
    }

    [Fact]
    public async Task A_queued_request_the_fake_never_grants_ends_as_a_stockout_that_leaves_nothing_behind()
    {
        var gcp = new FakeGcp().WithFlexStartExpiringAfter(TimeSpan.FromMilliseconds(300));
        var repo = new InMemoryRunRepository();

        var result = await Runner(gcp, repo).RunAsync(TestInputs.Request("job-f10", Spec("job-f10", flex: true)), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Failed);
        result.FailureKind.ShouldBe(CloudErrorKind.Stockout);
        LastRow(repo, "job-f10").ErrorCode.ShouldBe("stockout");
        (await gcp.FindByJobIdAsync("job-f10", CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_queued_create_outlives_the_ordinary_create_timeout_but_not_its_own_wait()
    {
        // The runner's ordinary create timeout is 100 ms here; the grant comes at 600 ms. Only the flex wait (plus margin) may bound this create.
        var gcp = new FakeGcp().WithFlexStartGrantedAfter(TimeSpan.FromMilliseconds(600));
        var repo = new InMemoryRunRepository();

        var result = await Runner(gcp, repo).RunAsync(TestInputs.Request("job-f11", Spec("job-f11", flex: true, wait: TimeSpan.FromSeconds(30))), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Completed, result.FailureMessage);
        gcp.CreatedSpecs.Count.ShouldBe(1, "a slow grant is not a second create");
    }

    [Fact]
    public async Task The_run_says_it_is_waiting_for_a_GPU_while_queued_and_says_when_that_ends()
    {
        var notices = new List<RunNotice>();
        var gcp = new FakeGcp().WithFlexStartGrantedAfter(TimeSpan.FromMilliseconds(300));
        var repo = new InMemoryRunRepository();

        var result = await Runner(gcp, repo, n => { lock (notices) { notices.Add(n); } }).RunAsync(TestInputs.Request("job-f12", Spec("job-f12", flex: true)), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Completed, result.FailureMessage);
        var until = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < until && notices.Count(n => n.Kind is RunNoticeKind.WaitingForGpu or RunNoticeKind.WaitingForGpuEnded) < 2)
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        List<RunNoticeKind> kinds;
        lock (notices)
        {
            kinds = notices.Where(n => n.Kind is RunNoticeKind.WaitingForGpu or RunNoticeKind.WaitingForGpuEnded).Select(n => n.Kind).ToList();
        }

        kinds.ShouldBe([RunNoticeKind.WaitingForGpu, RunNoticeKind.WaitingForGpuEnded]);
    }

    [Fact]
    public async Task An_on_demand_run_gives_no_waiting_notice()
    {
        var notices = new List<RunNotice>();
        var gcp = new FakeGcp();

        var result = await Runner(gcp, new InMemoryRunRepository(), n => { lock (notices) { notices.Add(n); } }).RunAsync(TestInputs.Request("job-f13", Spec("job-f13", flex: false)), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Completed, result.FailureMessage);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        lock (notices)
        {
            notices.ShouldNotContain(n => n.Kind == RunNoticeKind.WaitingForGpu);
        }
    }

    [Fact]
    public async Task A_cancel_while_queued_removes_the_pending_request_and_ends_Cancelled()
    {
        var gcp = new FakeGcp().WithFlexStartGrantedAfter(TimeSpan.FromMinutes(5));
        var repo = new InMemoryRunRepository();
        var runner = Runner(gcp, repo);
        using var cts = new CancellationTokenSource();
        var run = Task.Run(() => runner.RunAsync(TestInputs.Request("job-f14", Spec("job-f14", flex: true)), cts.Token), TestContext.Current.CancellationToken);

        await gcp.FlexStartQueued.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        (await gcp.FindByJobIdAsync("job-f14", CancellationToken.None)).ShouldNotBeEmpty("the pending request is there before the cancel");

        // The same order JobEngine uses: stop the run's driver first, then cancel the job.
        await cts.CancelAsync();
        await runner.CancelAsync("job-f14", CancellationToken.None);
        await run.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken).ContinueWith(_ => { }, TestContext.Current.CancellationToken);

        LastRow(repo, "job-f14").Phase.ShouldBe(JobPhase.Cancelled);
        (await gcp.FindByJobIdAsync("job-f14", CancellationToken.None)).ShouldBeEmpty("the pending request was cancelled");
    }

    [Fact]
    public async Task A_flex_run_asks_one_zone_only_because_a_queued_request_sits_in_the_zone_it_was_made_in()
    {
        var gcp = new FakeGcp().WithFlexStartGrantedAfter(TimeSpan.FromMilliseconds(100));
        var repo = new InMemoryRunRepository();

        var result = await Runner(gcp, repo).RunAsync(TestInputs.Request("job-f15", Spec("job-f15", flex: true), zones: ["us-central1-a", "us-central1-b", "us-east1-b"]), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Completed, result.FailureMessage);
        gcp.CreateAttempts.ShouldBe(1);
    }

    // ---- cold review: H1 the queue wait is not part of the run's own clock ----

    [Fact]
    public async Task A_queue_wait_is_not_charged_against_the_result_limit()
    {
        // MaxRunDuration 4 h (result limit 3 h 57 min). The request waits 3 h 40 min in the queue, then the worker needs 100 looks of a minute each:
        // measured from the request that is 5 h 20 min and a "result_timeout"; measured from the grant it is 1 h 40 min.
        var app = new VirtualTimeProvider();
        var gcp = new FakeGcp(app).WithWorker(FakeWorkerMode.HeartbeatingThenDone).WithFlexStartHeld();
        var repo = new InMemoryRunRepository();
        var runner = new CloudJobRunner(gcp, gcp, gcp, gcp, gcp, repo)
        {
            TimeProvider = app,
            ResultTimeout = null,
            FirstHeartbeatTimeout = TimeSpan.FromDays(2),
            HeartbeatStaleTimeout = TimeSpan.FromDays(2),
            CallTimeout = TimeSpan.FromSeconds(10),
            CreateTimeout = TimeSpan.FromSeconds(10),
            BootTimeout = TimeSpan.FromSeconds(10),
            LifecycleTimeout = TimeSpan.FromSeconds(1),
            LifecyclePollInterval = TimeSpan.FromMilliseconds(1),
            PollDelay = (_, _) =>
            {
                app.Advance(TimeSpan.FromMinutes(1));
                return Task.CompletedTask;
            },
        };
        var request = TestInputs.Request("job-f16", Spec("job-f16", flex: true, wait: TimeSpan.FromHours(3.8)));
        var run = Task.Run(() => runner.RunAsync(request, CancellationToken.None), TestContext.Current.CancellationToken);

        await gcp.FlexStartQueued.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        app.Advance(TimeSpan.FromHours(3) + TimeSpan.FromMinutes(40));
        gcp.GrantFlexStart();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        result.FailureCode.ShouldBeNull(result.FailureMessage);
        result.FinalPhase.ShouldBe(JobPhase.Completed, result.FailureMessage);
    }

    [Fact]
    public void A_short_run_limit_does_not_shorten_the_wait_for_a_card_because_the_run_clock_starts_at_the_grant()
    {
        // Review round 2 (L1): "wait <= maxRunDuration" was our invention, not a Compute rule we know of (THEORY, ToTest #92). The run limit counts from the grant (H1).
        Should.NotThrow(() => Spec("job-f17", flex: true, wait: TimeSpan.FromHours(5)).EnsurePreconditions());

        var inputs = new[] { new DnaEntropyGraph.Core.Contract.StagedInput(@"C:\x\a.gb", "a.gb") };
        var request = CloudJobRequestFactory.Create(new RunOptions { ModelId = "evo2_7b", RunTarget = "Cloud", WaitForGpu = true, MaxRunDurationMinutes = 30, WaitForGpuMinutes = 600 }, "20260101-000000-abcdef", new CloudScope(Project), "install-1", "0.1.0", null, inputs, @"C:\out");

        request.Spec.FlexStartWait.ShouldBe(TimeSpan.FromMinutes(600));
        request.Spec.MaxRunDuration.ShouldBeLessThan(request.Spec.FlexStartWait!.Value);
        Should.NotThrow(() => request.Spec.EnsurePreconditions());
    }

    // ---- cold review: M2 cancel and app exit while queued ----

    [Fact]
    public async Task A_cancel_while_queued_deletes_the_pending_request_by_its_own_name_even_when_the_label_listing_cannot_see_it()
    {
        var gcp = new FakeGcp().WithFlexStartHeld().WithQueuedRequestsHiddenFromLabelListing();
        var repo = new InMemoryRunRepository();
        var runner = Runner(gcp, repo);
        using var cts = new CancellationTokenSource();
        var run = Task.Run(() => runner.RunAsync(TestInputs.Request("job-f18", Spec("job-f18", flex: true)), cts.Token), TestContext.Current.CancellationToken);

        await gcp.FlexStartQueued.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        (await gcp.GetVmAsync("deg-job-f18", Zone, CancellationToken.None)).ShouldNotBeNull();
        (await gcp.FindByJobIdAsync("job-f18", CancellationToken.None)).ShouldBeEmpty("the listing cannot see it, by script");

        await cts.CancelAsync();
        await runner.CancelAsync("job-f18", CancellationToken.None);
        await run.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken).ContinueWith(_ => { }, TestContext.Current.CancellationToken);

        LastRow(repo, "job-f18").Phase.ShouldBe(JobPhase.Cancelled);
        (await gcp.GetVmAsync("deg-job-f18", Zone, CancellationToken.None)).ShouldBeNull("the pending request was deleted by name");
    }

    [Fact]
    public async Task A_queued_run_resumed_after_the_app_closed_adopts_the_pending_request_and_completes_without_a_second_create()
    {
        var gcp = new FakeGcp().WithFlexStartHeld();
        var repo = new InMemoryRunRepository();
        var request = TestInputs.Request("job-f19", Spec("job-f19", flex: true));
        using var closed = new CancellationTokenSource();
        var first = Task.Run(() => Runner(gcp, repo).RunAsync(request, closed.Token), TestContext.Current.CancellationToken);
        await gcp.FlexStartQueued.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

        // The app closes: the driver is gone, nobody cancels or deletes. The request stays queued on Google's side.
        await closed.CancelAsync();
        await first.ContinueWith(_ => { }, TestContext.Current.CancellationToken);

        var second = Task.Run(
            () => new CloudJobRunner(gcp, gcp, gcp, gcp, gcp, repo, null)
            {
                ResultPollInterval = TimeSpan.FromMilliseconds(1),
                ResultTimeout = TimeSpan.FromSeconds(20),
                CallTimeout = TimeSpan.FromSeconds(5),
                CreateTimeout = TimeSpan.FromSeconds(5),
                BootTimeout = TimeSpan.FromSeconds(20),
                LifecycleTimeout = TimeSpan.FromSeconds(2),
                LifecyclePollInterval = TimeSpan.FromMilliseconds(1),
            }.RunAsync(request, CancellationToken.None),
            TestContext.Current.CancellationToken);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        gcp.GrantFlexStart();
        var result = await second.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        result.FinalPhase.ShouldBe(JobPhase.Completed, result.FailureMessage);
        gcp.CreatedSpecs.Count.ShouldBe(1, "the resumed run adopted the pending request");
    }

    // ---- cold review: M3 the body on the wire ----

    [Fact]
    public void The_serialized_flex_start_body_carries_params_requestValidForDuration_and_an_on_demand_body_does_not()
    {
        var flex = Spec("job-f20", flex: true, wait: TimeSpan.FromHours(3));
        var onDemand = Spec("job-f21", flex: false);

        var flexJson = Google.Apis.Json.NewtonsoftJsonSerializer.Instance.Serialize(ComputeVmShape.Build(flex, Zone, flex.ToLabels(), "sa@my-project.iam.gserviceaccount.com"));
        var plainJson = Google.Apis.Json.NewtonsoftJsonSerializer.Instance.Serialize(ComputeVmShape.Build(onDemand, Zone, onDemand.ToLabels(), "sa@my-project.iam.gserviceaccount.com"));

        flexJson.ShouldContain("\"params\":{\"requestValidForDuration\":{");
        flexJson.ShouldContain("10800");
        flexJson.ShouldContain("\"provisioningModel\":\"FLEX_START\"");
        plainJson.ShouldNotContain("\"params\"");
    }

    // ---- cold review: M4 ordered notices ----

    [Fact]
    public async Task The_waiting_and_ended_notices_arrive_in_order_even_when_the_create_fails_at_once_and_the_first_handler_is_slow()
    {
        var order = new List<RunNoticeKind>();
        var gcp = new FakeGcp().WithCreateFailure(new CloudError("INTERNAL", null, "boom"), 1);
        var repo = new InMemoryRunRepository();

        var result = await Runner(
            gcp,
            repo,
            n =>
            {
                if (n.Kind == RunNoticeKind.WaitingForGpu)
                {
                    Thread.Sleep(150);
                }

                lock (order)
                {
                    order.Add(n.Kind);
                }
            }).RunAsync(TestInputs.Request("job-f22", Spec("job-f22", flex: true)), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Failed);
        var until = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < until && order.Count < 2)
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        lock (order)
        {
            order.ShouldBe([RunNoticeKind.WaitingForGpu, RunNoticeKind.WaitingForGpuEnded]);
        }
    }

    // ---- cold review: L5 one margin, from the spec, in both places ----

    [Fact]
    public async Task The_create_is_bounded_by_the_specs_own_margin_in_the_provisioner_and_the_spec_names_the_same_bound_for_the_gateway()
    {
        var spec = Spec("job-f23", flex: true, wait: TimeSpan.FromMilliseconds(200)) with { FlexStartMargin = TimeSpan.FromMilliseconds(100) };
        spec.FlexStartCreateBound.ShouldBe(TimeSpan.FromMilliseconds(300));
        Spec("job-f24", flex: true, wait: TimeSpan.FromHours(1)).FlexStartCreateBound.ShouldBe(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(5));

        var gcp = new FakeGcp().WithFlexStartGrantedAfter(TimeSpan.FromSeconds(30));
        var started = DateTime.UtcNow;

        var result = await Runner(gcp, new InMemoryRunRepository()).RunAsync(TestInputs.Request("job-f23", spec), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Failed);
        (DateTime.UtcNow - started).ShouldBeLessThan(TimeSpan.FromSeconds(15), "the 300 ms bound, not the 30 s grant, ended the create");
    }

    // ---- cold review round 2 ----

    [Fact]
    public async Task A_resumed_run_waits_out_a_request_still_queued_past_the_boot_timeout_and_makes_no_second_create()
    {
        var gcp = new FakeGcp().WithFlexStartHeld();
        var repo = new InMemoryRunRepository();
        var request = TestInputs.Request("job-f25", Spec("job-f25", flex: true));
        using var closed = new CancellationTokenSource();
        var first = Task.Run(() => Runner(gcp, repo).RunAsync(request, closed.Token), TestContext.Current.CancellationToken);
        await gcp.FlexStartQueued.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        await closed.CancelAsync();
        await first.ContinueWith(_ => { }, TestContext.Current.CancellationToken);

        var second = Task.Run(
            () => new CloudJobRunner(gcp, gcp, gcp, gcp, gcp, repo, null)
            {
                ResultPollInterval = TimeSpan.FromMilliseconds(1),
                ResultTimeout = TimeSpan.FromSeconds(20),
                CallTimeout = TimeSpan.FromSeconds(5),
                CreateTimeout = TimeSpan.FromSeconds(5),
                BootTimeout = TimeSpan.FromMilliseconds(400),
                LifecycleTimeout = TimeSpan.FromSeconds(2),
                LifecyclePollInterval = TimeSpan.FromMilliseconds(1),
            }.RunAsync(request, CancellationToken.None),
            TestContext.Current.CancellationToken);
        await Task.Delay(1500, TestContext.Current.CancellationToken); // well past the 400 ms boot timeout, still inside the request's own wait
        gcp.GrantFlexStart();
        var result = await second.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        result.FinalPhase.ShouldBe(JobPhase.Completed, result.FailureMessage);
        gcp.CreatedSpecs.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_cancel_while_queued_still_withdraws_the_waiting_line()
    {
        var notices = new List<RunNoticeKind>();
        var gcp = new FakeGcp().WithFlexStartHeld();
        var repo = new InMemoryRunRepository();
        var runner = Runner(gcp, repo, n => { lock (notices) { notices.Add(n.Kind); } });
        using var cts = new CancellationTokenSource();
        var run = Task.Run(() => runner.RunAsync(TestInputs.Request("job-f26", Spec("job-f26", flex: true)), cts.Token), TestContext.Current.CancellationToken);
        await gcp.FlexStartQueued.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

        await cts.CancelAsync();
        await runner.CancelAsync("job-f26", CancellationToken.None);
        await run.ContinueWith(_ => { }, TestContext.Current.CancellationToken);
        var until = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < until && notices.Count < 2)
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        lock (notices)
        {
            notices.ShouldBe([RunNoticeKind.WaitingForGpu, RunNoticeKind.WaitingForGpuEnded]);
        }
    }

    [Fact]
    public async Task A_handler_that_blocks_for_one_job_does_not_hold_back_another_jobs_notices()
    {
        var seen = new List<(string Job, RunNoticeKind Kind)>();
        using var gate = new ManualResetEventSlim(false);
        var gcp = new FakeGcp().WithFlexStartGrantedAfter(TimeSpan.FromMilliseconds(200));
        var runner = Runner(
            gcp,
            new InMemoryRunRepository(),
            n =>
            {
                if (n.JobId == "job-f27a" && n.Kind == RunNoticeKind.WaitingForGpu)
                {
                    gate.Wait(TimeSpan.FromSeconds(20));
                }

                lock (seen)
                {
                    seen.Add((n.JobId, n.Kind));
                }
            });

        var a = Task.Run(() => runner.RunAsync(TestInputs.Request("job-f27a", Spec("job-f27a", flex: true), zones: ["us-central1-a"]), CancellationToken.None), TestContext.Current.CancellationToken);
        var b = Task.Run(() => runner.RunAsync(TestInputs.Request("job-f27b", Spec("job-f27b", flex: true), zones: ["us-central1-b"]), CancellationToken.None), TestContext.Current.CancellationToken);

        var until = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < until)
        {
            lock (seen)
            {
                if (seen.Count(s => s.Job == "job-f27b") >= 2)
                {
                    break;
                }
            }

            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        List<RunNoticeKind> bKinds;
        lock (seen)
        {
            bKinds = seen.Where(s => s.Job == "job-f27b").Select(s => s.Kind).ToList();
        }

        gate.Set();
        await Task.WhenAll(a, b).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        bKinds.ShouldBe([RunNoticeKind.WaitingForGpu, RunNoticeKind.WaitingForGpuEnded], "job B's notices arrived while job A's handler was still blocked");
    }

    [Fact]
    public async Task A_queued_request_is_forgotten_when_its_run_ends_terminal_even_if_its_create_timed_out_unconfirmed()
    {
        var spec = Spec("job-f28", flex: true, wait: TimeSpan.FromMilliseconds(100)) with { FlexStartMargin = TimeSpan.FromMilliseconds(100) };
        var gcp = new FakeGcp().WithFlexStartHeld();
        var repo = new InMemoryRunRepository();
        var runner = Runner(gcp, repo);

        var result = await runner.RunAsync(TestInputs.Request("job-f28", spec), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Failed);
        runner.PendingQueuedRequestCount.ShouldBe(0);
    }

    // ---- cold review round 3 ----

    /// <summary>Queues a flex request with a first runner, closes it (the app closes: nothing cancels or deletes), and returns the request for a second runner to adopt.</summary>
    private static async Task<CloudJobRequest> QueueThenCloseAppAsync(FakeGcp gcp, InMemoryRunRepository repo, string jobId, VmSpec spec)
    {
        var request = TestInputs.Request(jobId, spec);
        using var closed = new CancellationTokenSource();
        var first = Task.Run(() => Runner(gcp, repo).RunAsync(request, closed.Token));
        await gcp.FlexStartQueued.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        await closed.CancelAsync();
        await first.ContinueWith(_ => { }, TestContext.Current.CancellationToken);
        return request;
    }

    /// <summary>A resumed runner on a virtual clock that is a long way from the platform's, advancing one minute per look; <paramref name="onPoll"/> sees the 1-based look number.</summary>
    private static CloudJobRunner VirtualResumedRunner(FakeGcp gcp, InMemoryRunRepository repo, VirtualTimeProvider app, Action<int> onPoll)
    {
        var polls = 0;
        return new CloudJobRunner(gcp, gcp, gcp, gcp, gcp, repo)
        {
            TimeProvider = app,
            ResultTimeout = null,
            FirstHeartbeatTimeout = TimeSpan.FromDays(9),
            HeartbeatStaleTimeout = TimeSpan.FromDays(9),
            CallTimeout = TimeSpan.FromSeconds(10),
            CreateTimeout = TimeSpan.FromSeconds(10),
            BootTimeout = TimeSpan.FromMinutes(8),
            LifecycleTimeout = TimeSpan.FromSeconds(1),
            LifecyclePollInterval = TimeSpan.FromMilliseconds(1),
            PollDelay = (_, _) =>
            {
                onPoll(Interlocked.Increment(ref polls));
                app.Advance(TimeSpan.FromMinutes(1));
                return Task.CompletedTask;
            },
        };
    }

    /// <summary>Grants the held request and waits until the fake's server has made the VM, so a virtual-time loop that polls in microseconds cannot outrun the grant.</summary>
    private static void GrantAndWait(FakeGcp gcp, string vmName)
    {
        gcp.GrantFlexStart();
        SpinWait.SpinUntil(() => gcp.GetVmAsync(vmName, Zone, CancellationToken.None).Result is { Status: "RUNNING" }, TimeSpan.FromSeconds(10)).ShouldBeTrue("the grant took effect");
        Thread.Sleep(100); // the fake writes the simulated worker's files right after it publishes RUNNING
    }

    [Fact]
    public async Task F2i_an_adopted_request_still_queued_after_its_own_window_ends_in_a_boot_timeout()
    {
        var spec = Spec("job-f30", flex: true, wait: TimeSpan.FromMilliseconds(100)) with { FlexStartMargin = TimeSpan.FromMilliseconds(100) };
        var gcp = new FakeGcp().WithFlexStartHeld();
        var repo = new InMemoryRunRepository();
        var request = await QueueThenCloseAppAsync(gcp, repo, "job-f30", spec);

        var second = new CloudJobRunner(gcp, gcp, gcp, gcp, gcp, repo, null)
        {
            ResultPollInterval = TimeSpan.FromMilliseconds(1),
            ResultTimeout = TimeSpan.FromSeconds(20),
            CallTimeout = TimeSpan.FromSeconds(5),
            CreateTimeout = TimeSpan.FromSeconds(5),
            BootTimeout = TimeSpan.FromMilliseconds(300),
            LifecycleTimeout = TimeSpan.FromSeconds(2),
            LifecyclePollInterval = TimeSpan.FromMilliseconds(1),
        };
        var result = await Task.Run(() => second.RunAsync(request, CancellationToken.None), TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        result.FinalPhase.ShouldBe(JobPhase.Failed);
        result.FailureCode.ShouldBe(RunErrorCodes.VmUnhealthy);
        result.FailureMessage!.ShouldContain("did not finish starting");
        (await gcp.GetVmAsync("deg-job-f30", Zone, CancellationToken.None)).ShouldBeNull("the request that never started was deleted");
    }

    [Fact]
    public async Task F2ii_without_server_time_the_window_runs_from_the_adoption_not_from_a_platform_timestamp_on_another_clock()
    {
        // The laptop's clock is three days away from the platform's, and the answers carry no server time: subtracting the platform's CreatedAt from the laptop's now
        // would call a request queued a moment ago "past its window" and give it up after the 8-minute boot timeout.
        var platform = new VirtualTimeProvider();
        var app = new VirtualTimeProvider();
        app.Advance(TimeSpan.FromDays(3));
        var gcp = new FakeGcp(platform).WithFlexStartHeld().WithoutServerTime();
        var repo = new InMemoryRunRepository();
        var request = await QueueThenCloseAppAsync(gcp, repo, "job-f31", Spec("job-f31", flex: true));

        var result = await Task.Run(() => VirtualResumedRunner(gcp, repo, app, n =>
            {
                platform.Advance(TimeSpan.FromMinutes(1)); // the fake's worker finishes on the platform's clock
                if (n == 30)
                {
                    GrantAndWait(gcp, "deg-job-f31");
                }
            }).RunAsync(request, CancellationToken.None), TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        result.FinalPhase.ShouldBe(JobPhase.Completed, result.FailureMessage);
        gcp.CreatedSpecs.Count.ShouldBe(1);
    }

    [Fact]
    public async Task F2iii_a_fresh_flex_create_that_is_granted_and_then_hangs_booting_keeps_the_boot_hang_detector()
    {
        var gcp = new FakeGcp().WithFlexStartGrantedAfter(TimeSpan.FromMilliseconds(50)).WithBootStatusPolls(1_000_000);
        var repo = new InMemoryRunRepository();

        var result = await Task.Run(() => Runner(gcp, repo).RunAsync(TestInputs.Request("job-f32", Spec("job-f32", flex: true)), CancellationToken.None), TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        result.FinalPhase.ShouldBe(JobPhase.Failed);
        result.FailureCode.ShouldBe(RunErrorCodes.VmUnhealthy);
        result.FailureMessage!.ShouldContain("did not finish starting");
    }

    [Fact]
    public async Task F2iv_failed_looks_while_an_adopted_request_is_queued_do_not_count_toward_the_boot_timeout()
    {
        var app = new VirtualTimeProvider();
        var gcp = new FakeGcp(app).WithFlexStartHeld();
        var repo = new InMemoryRunRepository();
        var request = await QueueThenCloseAppAsync(gcp, repo, "job-f33", Spec("job-f33", flex: true));
        gcp.WithGetVmFailures(503, 25);

        var result = await Task.Run(() => VirtualResumedRunner(gcp, repo, app, n => { if (n == 28) { GrantAndWait(gcp, "deg-job-f33"); } }).RunAsync(request, CancellationToken.None), TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        result.FinalPhase.ShouldBe(JobPhase.Completed, result.FailureMessage);
    }

    [Fact]
    public async Task F2v_an_adopted_request_that_expires_and_disappears_leaves_the_boot_wait_at_once()
    {
        var gcp = new FakeGcp().WithFlexStartExpiringAfter(TimeSpan.FromMilliseconds(700));
        var repo = new InMemoryRunRepository();
        var request = await QueueThenCloseAppAsync(gcp, repo, "job-f34", Spec("job-f34", flex: true));

        var second = new CloudJobRunner(gcp, gcp, gcp, gcp, gcp, repo, null)
        {
            ResultPollInterval = TimeSpan.FromMilliseconds(1),
            ResultTimeout = TimeSpan.FromSeconds(20),
            CallTimeout = TimeSpan.FromSeconds(5),
            CreateTimeout = TimeSpan.FromSeconds(5),
            BootTimeout = TimeSpan.FromSeconds(40),
            LifecycleTimeout = TimeSpan.FromSeconds(2),
            LifecyclePollInterval = TimeSpan.FromMilliseconds(1),
        };
        var result = await Task.Run(() => second.RunAsync(request, CancellationToken.None), TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        result.FinalPhase.ShouldBe(JobPhase.Failed);
        result.FailureMessage!.ShouldNotContain("did not finish starting", Case.Insensitive, "a vanished request is a lost VM, not a boot that timed out");
    }

    [Fact]
    public async Task F3_a_run_that_ends_terminal_forgets_a_queued_request_whose_unconfirmed_settle_left_it_remembered()
    {
        var spec = Spec("job-f35", flex: true, wait: TimeSpan.FromMilliseconds(100)) with { FlexStartMargin = TimeSpan.FromMilliseconds(100) };
        var gcp = new FakeGcp().WithFlexStartHeld().WithSilentNoOpDeletes();
        var runner = Runner(gcp, new InMemoryRunRepository());

        var result = await runner.RunAsync(TestInputs.Request("job-f35", spec), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Failed);
        result.FailureMessage.ShouldNotBeNull();
        (await gcp.GetVmAsync("deg-job-f35", Zone, CancellationToken.None)).ShouldNotBeNull("the silent delete left the request there, so the settle note was non-null and the entry survived the create");
        runner.PendingQueuedRequestCount.ShouldBe(0);
    }

    [Fact]
    public async Task F6_no_per_job_notice_chain_outlives_both_jobs_ended_notices()
    {
        var gcp = new FakeGcp().WithFlexStartGrantedAfter(TimeSpan.FromMilliseconds(100));
        var runner = Runner(gcp, new InMemoryRunRepository(), _ => { });

        var a = Task.Run(() => runner.RunAsync(TestInputs.Request("job-f36a", Spec("job-f36a", flex: true), zones: ["us-central1-a"]), CancellationToken.None), TestContext.Current.CancellationToken);
        var b = Task.Run(() => runner.RunAsync(TestInputs.Request("job-f36b", Spec("job-f36b", flex: true), zones: ["us-central1-b"]), CancellationToken.None), TestContext.Current.CancellationToken);
        await Task.WhenAll(a, b).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        var until = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < until && runner.NoticeChainCount > 0)
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        runner.NoticeChainCount.ShouldBe(0);
    }
}
