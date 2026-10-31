using System.Reflection;
using System.Xml.Linq;
using DnaEntropyGraph.App;
using DnaEntropyGraph.App.Startup;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Persistence;
using DnaEntropyGraph.Presentation.Services;
using DnaEntropyGraph.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// Issue #260, wired-to-nothing: the project switcher card is only real if the XAML binds the view model's list, current project, Switch command and status,
/// the page loads the list when it opens (a combo that is never filled shows nothing) and lets go of the account when it closes, and the app registers what the card needs.
/// </summary>
public class ProjectSwitchWiringTests
{
    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([RepoPaths.AppRoot, "src", "DnaEntropyGraph.App", .. parts]));

    [Theory]
    [InlineData("ItemsSource=\"{x:Bind ProjectSwitch.ProjectIds}\"")]
    [InlineData("SelectedItem=\"{x:Bind ProjectSwitch.ChosenProjectId, Mode=TwoWay}\"")]
    [InlineData("Command=\"{x:Bind ProjectSwitch.SwitchCommand}\"")]
    [InlineData("Text=\"{x:Bind ProjectSwitch.CurrentProjectId, Mode=OneWay}\"")]
    [InlineData("Text=\"{x:Bind ProjectSwitch.BucketText, Mode=OneWay}\"")]
    [InlineData("Text=\"{x:Bind ProjectSwitch.Status, Mode=OneWay}\"")]
    public void The_settings_page_binds_the_switcher_card(string binding)
    {
        Read("Views", "SettingsPage.xaml").ShouldContain(binding);
    }

    [Fact]
    public void The_page_fills_the_list_every_time_it_opens_and_lets_go_of_the_account_when_it_closes()
    {
        var code = Read("Views", "SettingsPage.xaml.cs");

        code.ShouldContain("GetRequiredService<ProjectSwitchSettingsViewModel>()");
        code.ShouldContain("ProjectSwitch.LoadAsync()");
        code.ShouldContain("OnNavigatedFrom");
        code.ShouldContain("ProjectSwitch.Detach()");
    }

    private static ServiceProvider BuildRealProvider(string appDataRoot)
    {
        var services = new ServiceCollection();
        services.AddDnaEntropyGraph(appDataRoot: appDataRoot);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private static T Field<T>(object owner, string name)
        => (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;

    [Fact]
    public void The_real_container_resolves_the_switcher_and_hands_the_one_store_to_every_writer()
    {
        // A resolved object graph, not source text: a registration that is only mentioned in a comment, or a consumer built without the store, fails here.
        var root = Path.Combine(Path.GetTempPath(), $"deg-guard-appdata-{Guid.NewGuid():n}");
        try
        {
            using var provider = BuildRealProvider(root);

            var store = provider.GetRequiredService<IProjectSettingsStore>();
            store.ShouldBeOfType<ProjectSettingsStore>();
            provider.GetRequiredService<ProjectSwitcher>().ShouldNotBeNull();
            provider.GetRequiredService<ProjectSwitchSettingsViewModel>().ShouldNotBeNull();
            Field<IProjectSettingsStore>(provider.GetRequiredService<JobReconciler>(), "_projectSettings").ShouldBeSameAs(store, "the reconciler remembers a resumed run's bucket only if it is given the store");
            Field<IProjectSettingsStore>(provider.GetRequiredService<JobEngine>(), "_projects").ShouldBeSameAs(store);
            Field<IProjectSettingsStore?>(provider.GetRequiredService<WizardViewModel>(), "_projectStore").ShouldBeSameAs(store, "a project chosen in the wizard is recorded for the account, so a later switch can offer it again");
            Field<ProjectSwitcher>(provider.GetRequiredService<ProjectSwitchSettingsViewModel>(), "_switcher").ShouldNotBeNull();
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch (IOException)
            {
                // A temp folder still held by the test host is cleaned by the OS later; it is not a failure of the wiring.
            }
        }
    }

    [Theory]
    [InlineData("SettingsProjectHeader.Text")]
    [InlineData("SettingsProjectExplainer.Text")]
    [InlineData("ProjectSwitchBox.Header")]
    [InlineData("ProjectSwitchButton.Content")]
    [InlineData("ProjectCurrentLabel.Text")]
    [InlineData("ProjectSwitched")]
    [InlineData("ProjectBucketKnown")]
    [InlineData("ProjectBucketNone")]
    [InlineData("ProjectListFailed")]
    [InlineData("ProjectSwitchFailed")]
    [InlineData("AuthError_PROJECT_UNREACHABLE")]
    [InlineData("AuthError_ACCOUNT_CHANGED_DURING_SWITCH")]
    [InlineData("AuthAction_ChooseAnotherProject")]
    public void Every_string_the_card_shows_is_in_the_resources_and_has_no_em_or_en_dash(string key)
    {
        var resw = Read("Strings", "en-US", "Resources.resw");
        var start = resw.IndexOf($"<data name=\"{key}\"", StringComparison.Ordinal);
        start.ShouldBeGreaterThan(-1, key + " is missing from Resources.resw");
        var value = resw[start..resw.IndexOf("</data>", start, StringComparison.Ordinal)];

        value.ShouldNotContain("—");
        value.ShouldNotContain("–");
    }
}

/// <summary>The real English the person reads for a refused switch: the message must not already say what the appended action button says.</summary>
public class ProjectSwitchStatusCopyTests
{
    private static Dictionary<string, string> RealStrings()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in RepoPaths.AllReswFiles)
        {
            foreach (var data in XDocument.Load(file).Descendants("data"))
            {
                values[data.Attribute("name")!.Value] = data.Element("value")?.Value ?? string.Empty;
            }
        }

        return values;
    }

    [Theory]
    [InlineData(AuthErrorCodes.ProjectUnreachable)]
    [InlineData(AuthErrorCodes.AccountChangedDuringSwitch)]
    [InlineData(AuthErrorCodes.NetworkUnavailable)]
    [InlineData(AuthErrorCodes.ProjectSaveFailed)]
    public async Task A_refused_switch_names_its_action_exactly_once_using_the_real_resource_strings(string code)
    {
        var real = RealStrings();
        real.Count.ShouldBeGreaterThan(500, "vacuity: the real Resources.resw was read");
        var strings = Substitute.For<IStringResourceProvider>();
        strings.GetString(Arg.Any<string>()).Returns(call => real.TryGetValue(call.Arg<string>(), out var v) ? v : throw new InvalidOperationException("no resource " + call.Arg<string>()));
        var account = Substitute.For<IGcpAccount>();
        account.SelectedProjectId.Returns("project-a");
        account.CurrentAccount.Returns(new AccountInfo("sub-1", "a@example.test"));
        var store = Substitute.For<IProjectSettingsStore>();
        store.GetKnownAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns([new ProjectSettings("project-a"), new ProjectSettings("project-b")]);
        var runs = Substitute.For<IRunRepository>();
        runs.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<RunRecord>>([]));
        var setup = Substitute.For<IProjectSetupGateway>();
        setup.GetProjectStateAsync(Arg.Any<CloudScope>(), Arg.Any<CancellationToken>()).Returns<Task<ProjectLifecycleState>>(_ => throw new AccountAuthException(code));
        var vm = new ProjectSwitchSettingsViewModel(new ProjectSwitcher(account, store, runs, setup), account, strings);
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.ChosenProjectId = "project-b";

        await vm.SwitchCommand.ExecuteAsync(null);

        var action = real[AuthErrorCodes.ActionResourceKey(code)!];
        vm.Status.ShouldStartWith(real[AuthErrorCodes.ResourceKey(code)]);
        vm.Status.ShouldContain(action);
        vm.Status.Split(action).Length.ShouldBe(2, $"the action '{action}' must appear once in: {vm.Status}");
    }

    [Fact]
    public async Task A_project_Google_will_not_open_reads_the_message_then_the_one_action()
    {
        var real = RealStrings();
        var strings = Substitute.For<IStringResourceProvider>();
        strings.GetString(Arg.Any<string>()).Returns(call => real[call.Arg<string>()]);
        var account = Substitute.For<IGcpAccount>();
        account.SelectedProjectId.Returns("project-a");
        account.CurrentAccount.Returns(new AccountInfo("sub-1", "a@example.test"));
        var store = Substitute.For<IProjectSettingsStore>();
        store.GetKnownAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns([new ProjectSettings("project-a"), new ProjectSettings("project-b")]);
        var runs = Substitute.For<IRunRepository>();
        runs.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<RunRecord>>([]));
        var setup = Substitute.For<IProjectSetupGateway>();
        setup.GetProjectStateAsync(Arg.Any<CloudScope>(), Arg.Any<CancellationToken>()).Returns(ProjectLifecycleState.NotFound);
        var vm = new ProjectSwitchSettingsViewModel(new ProjectSwitcher(account, store, runs, setup), account, strings);
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.ChosenProjectId = "project-b";

        await vm.SwitchCommand.ExecuteAsync(null);

        vm.Status.ShouldBe(real["AuthError_PROJECT_UNREACHABLE"] + " Choose another project");
    }
}
