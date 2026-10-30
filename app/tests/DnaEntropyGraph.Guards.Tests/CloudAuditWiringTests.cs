using DnaEntropyGraph.App.Startup;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// Issue #245's observable, through the production registration: a mutation made through the gateway the container hands out lands as
/// a line in the audit file under the data root, and the Settings button points at that same file.
/// </summary>
public class CloudAuditWiringTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "deg-auditwire-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task A_mutation_through_the_containers_gateway_is_written_to_the_audit_file_and_a_read_is_not()
    {
        var services = new ServiceCollection();
        services.AddDnaEntropyGraph(appDataRoot: _root, cloud: CloudBackend.Developer);
        using var provider = services.BuildServiceProvider();
        var setup = provider.GetRequiredService<IProjectSetupGateway>();
        var file = provider.GetRequiredService<AppDataRoot>().CloudAuditLogFile;

        _ = await setup.IsComputeApiEnabledAsync("p1", CancellationToken.None);
        File.Exists(file).ShouldBeFalse("a read is not logged");

        await setup.EnableComputeApiAsync("p1", CancellationToken.None);

        var lines = File.ReadAllLines(file);
        lines.Length.ShouldBe(1);
        lines[0].ShouldContain("\"operation\":\"ProjectSetup.EnableComputeApi\"");
        lines[0].ShouldContain("\"outcome\":\"ok\"");
        provider.GetRequiredService<CloudActivityLogSettingsViewModel>().CloudActivityLogPath.ShouldBe(file);
    }
}
