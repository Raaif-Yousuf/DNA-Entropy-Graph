// Issue #830 milestone 3: a run keeps the account it started under, whichever account is current later, and a 401 refreshes the account of the call that got it.
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

public sealed class RunAccountScopeTests
{
    private const string Project = "my-project";
    private const string Zone = "us-central1-a";

    private static VmSpec Spec(string jobId) => new(Project, "install-1", jobId, "evo2_7b", "0.1.0", "stop", "g2-standard-8", TimeSpan.FromHours(4), "DELETE");

    [Fact]
    public async Task Switching_account_mid_run_leaves_every_call_of_the_run_and_its_cancel_on_the_account_it_started_under()
    {
        var gcp = new FakeGcp().WithAccount("sub-a", "a@example.org", Project).WithAccount("sub-b", "b@example.org", Project).WithWorker(FakeWorkerMode.Never);
        var repo = new InMemoryRunRepository();
        var runner = new CloudJobRunner(gcp, gcp, gcp, gcp, gcp, repo, null)
        {
            ResultPollInterval = TimeSpan.FromMilliseconds(1),
            ResultTimeout = TimeSpan.FromSeconds(30),
            CallTimeout = TimeSpan.FromMilliseconds(200),
            LifecycleTimeout = TimeSpan.FromMilliseconds(200),
        };
        var scopeA = new CloudScope(Project, "sub-a");
        var request = TestInputs.Request("job-sw", Spec("job-sw")) with { Scope = scopeA };
        var run = Task.Run(() => runner.RunAsync(request, CancellationToken.None), TestContext.Current.CancellationToken);
        var until = DateTime.UtcNow.AddSeconds(15);
        while (!repo.AllRecordedInOrder.Any(r => r.JobId == "job-sw" && r.Phase == JobPhase.Running))
        {
            (DateTime.UtcNow < until).ShouldBeTrue("the run never reached Running");
            await Task.Delay(2, TestContext.Current.CancellationToken);
        }

        await gcp.SwitchAccountAsync("sub-b", CancellationToken.None); // the person signs in as another account while the run goes on
        await runner.CancelAsync(scopeA, "job-sw", CancellationToken.None); // the engine resolves the run's own scope from its row
        await run.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        gcp.CurrentAccount!.Sub.ShouldBe("sub-b");
        gcp.Calls.Count.ShouldBeGreaterThan(5);
        gcp.Calls.ShouldAllBe(c => c.AccountSub == "sub-a", "no call of the run may use the account that became current");
        gcp.CallsTo("DeleteVm").ShouldNotBeEmpty();
    }

    [Fact]
    public async Task A_401_on_a_call_made_as_an_account_refreshes_that_account_not_the_current_one()
    {
        var gcp = new FakeGcp().WithAccount("sub-a", "a@example.org", Project).WithAccount("sub-b", "b@example.org", Project).WithUnauthorized(1);
        await gcp.SwitchAccountAsync("sub-b", CancellationToken.None);
        var pipeline = new CloudCallPipeline(new CloudRetryOptions { MaxRetryAttempts = 2, BaseDelay = TimeSpan.Zero, MaxDelay = TimeSpan.Zero }, gcp, new CloudRetryLog());
        var compute = new ResilientComputeGateway(gcp, pipeline);

        await compute.GetVmAsync(new CloudScope(Project, "sub-a"), "deg-job-1", Zone, CancellationToken.None);

        gcp.RefreshedAccounts.ShouldBe(["sub-a"]);
    }
}
