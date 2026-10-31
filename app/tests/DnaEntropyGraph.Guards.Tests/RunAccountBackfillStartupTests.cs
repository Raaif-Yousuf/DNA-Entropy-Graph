using DnaEntropyGraph.App.Startup;
using DnaEntropyGraph.Cloud;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// Issue #830 review M1: a run row from before <c>Runs.AccountSub</c> existed has no account. The migration cannot know it (nothing writes
/// <c>Projects.AccountSub</c>), so at the first launch with an account signed in, every NULL cloud row is given that account once, then a marker says
/// it is done. Driven through <see cref="AppStartup.BeginAsync"/> on the production container with a real SQLite file: a backfill that is registered
/// but never called leaves the rows NULL here.
/// </summary>
public sealed class RunAccountBackfillStartupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "deg-backfill-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // SQLite may still hold the file for a moment.
        }
    }

    private ServiceProvider Build(bool signedIn)
    {
        var services = new ServiceCollection();
        services.AddDnaEntropyGraph(appDataRoot: _root, cloud: CloudBackend.Fake);
        services.AddSingleton<FakeGcp>(_ => signedIn ? new FakeGcp().WithSelectedProject("my-project") : new FakeGcp().WithSignedOut());
        // The Fake backend signs in through the real account service; the scripted account is the FakeGcp one.
        services.AddSingleton<IGcpAccount>(sp => sp.GetRequiredService<FakeGcp>());
        return services.BuildServiceProvider();
    }

    private static Task SeedAsync(ServiceProvider provider, string jobId, string target, string? accountSub = null)
        => provider.GetRequiredService<IRunRepository>().UpsertAsync(
            new RunRecord(jobId, JobPhase.Completed, DateTimeOffset.UtcNow.AddHours(-2), Target: target, FinishedAt: DateTimeOffset.UtcNow.AddHours(-1), AccountSub: accountSub),
            CancellationToken.None);

    private static async Task<RunRecord> RowAsync(ServiceProvider provider, string jobId)
        => (await provider.GetRequiredService<IRunRepository>().GetAllAsync(CancellationToken.None)).Single(r => r.JobId == jobId);

    [Fact]
    public async Task The_first_launch_with_an_account_signed_in_gives_every_unassigned_cloud_row_that_account_and_leaves_the_rest()
    {
        using var provider = Build(signedIn: true);
        await SeedAsync(provider, "old-cloud", "cloud");
        await SeedAsync(provider, "old-local", "local");
        await SeedAsync(provider, "has-account", "cloud", "someone-else");

        await AppStartup.BeginAsync(provider, CancellationToken.None);

        (await RowAsync(provider, "old-cloud")).AccountSub.ShouldBe(FakeGcp.DefaultAccountSub);
        (await RowAsync(provider, "old-local")).AccountSub.ShouldBeNull("a This PC run has no cloud side and no account");
        (await RowAsync(provider, "has-account")).AccountSub.ShouldBe("someone-else");
    }

    [Fact]
    public async Task It_runs_once_so_a_row_that_appears_later_is_never_guessed()
    {
        using var provider = Build(signedIn: true);
        await AppStartup.BeginAsync(provider, CancellationToken.None);
        await SeedAsync(provider, "later-null", "cloud");

        await AppStartup.BeginAsync(provider, CancellationToken.None);

        (await RowAsync(provider, "later-null")).AccountSub.ShouldBeNull();
    }

    [Fact]
    public async Task With_no_account_signed_in_the_rows_stay_null_and_the_next_launch_with_one_assigns_them()
    {
        using (var signedOut = Build(signedIn: false))
        {
            await SeedAsync(signedOut, "old-cloud", "cloud");
            await AppStartup.BeginAsync(signedOut, CancellationToken.None);
            (await RowAsync(signedOut, "old-cloud")).AccountSub.ShouldBeNull();
        }

        using var signedIn = Build(signedIn: true);
        await AppStartup.BeginAsync(signedIn, CancellationToken.None);

        (await RowAsync(signedIn, "old-cloud")).AccountSub.ShouldBe(FakeGcp.DefaultAccountSub);
    }
}
