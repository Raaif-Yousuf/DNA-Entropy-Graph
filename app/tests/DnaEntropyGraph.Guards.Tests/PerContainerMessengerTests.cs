using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.Messaging;
using DnaEntropyGraph.App.Startup;
using DnaEntropyGraph.Presentation.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// Issue #825: the container registered the process-wide static messenger, so a SettingsResetMessage sent by one test (or one
/// container) reached every live picker in every other container, and RunTargetPickerSharingTests flaked under parallel classes.
/// </summary>
public sealed class PerContainerMessengerTests : IDisposable
{
    private readonly string _rootA = Path.Combine(Path.GetTempPath(), "deg-msgr-a-" + Guid.NewGuid().ToString("N"));
    private readonly string _rootB = Path.Combine(Path.GetTempPath(), "deg-msgr-b-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        foreach (var root in new[] { _rootA, _rootB })
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static ServiceProvider Build(string root)
    {
        var services = new ServiceCollection();
        services.AddDnaEntropyGraph(appDataRoot: root, cloud: CloudBackend.Fake);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Two_containers_do_not_receive_each_others_messages()
    {
        using var a = Build(_rootA);
        using var b = Build(_rootB);
        var recipient = new object();
        var received = 0;
        b.GetRequiredService<IMessenger>().Register<object, SettingsResetMessage>(recipient, (_, _) => received++);

        a.GetRequiredService<IMessenger>().Send(new SettingsResetMessage());

        received.ShouldBe(0);
        a.GetRequiredService<IMessenger>().ShouldNotBeSameAs(b.GetRequiredService<IMessenger>());

        b.GetRequiredService<IMessenger>().Send(new SettingsResetMessage());
        received.ShouldBe(1);
    }

    [Fact]
    public void Nothing_in_app_src_uses_the_static_messengers()
    {
        // The rule, not today's state: ObservableRecipient and friends default to WeakReferenceMessenger.Default, so a reference
        // here would reopen the cross-container leak. Everything takes IMessenger from DI.
        var pattern = new Regex(@"\b(Weak|Strong)ReferenceMessenger\s*\.\s*Default\b", RegexOptions.CultureInvariant);
        var offenders = RepoPaths.AllCSharpFiles
            .SelectMany(path => File.ReadLines(path)
                .Select((line, i) => (path, line, n: i + 1))
                .Where(x => !x.line.TrimStart().StartsWith("//", StringComparison.Ordinal) && pattern.IsMatch(x.line))
                .Select(x => $"{Path.GetRelativePath(RepoPaths.AppRoot, x.path)}:{x.n}"))
            .ToArray();

        offenders.ShouldBeEmpty("Use the IMessenger from DI, never the process-wide static (#825).");
    }
}
