using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Presentation.Services;
using DnaEntropyGraph.Presentation.ViewModels;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// Issue #148 review round 4 and Hard Rule 13: the budget group's copy is read from the REAL Resources.resw. Every Budget key has exactly the placeholders the code
/// passes arguments for, and the sentences the view model builds come out whole (no leftover brace, every argument shown).
/// </summary>
public sealed class BudgetCopyTests
{
    /// <summary>
    /// The number of placeholders each formatted Budget key USES (its highest {n} plus one); every other Budget key is plain text. The check is exact on the copy. The code may pass MORE
    /// arguments than a key uses, and only where listed in <see cref="ExtraArgumentsPassed"/> with the reason: string.Format ignores a surplus argument, so a request text that names no amount is safe.
    /// </summary>
    private static readonly Dictionary<string, int> PassedArguments = new(StringComparer.Ordinal)
    {
        ["BudgetStatusExists"] = 2,
        ["BudgetSaved"] = 2,
        ["BudgetStatusExistsNoAmount"] = 1,
        ["BudgetRequestText"] = 2,
        ["BudgetRequestText_OrgPolicy"] = 2,
        ["BudgetRequestText_ApiRead"] = 1,
        ["BudgetThresholdItem"] = 1,
        ["BudgetThresholdsAnd"] = 2,
    };

    /// <summary>Keys the code formats with more arguments than they use. CopyRequest passes (project, amount) to every request text; this one names no amount.</summary>
    private static readonly Dictionary<string, int> ExtraArgumentsPassed = new(StringComparer.Ordinal)
    {
        ["BudgetRequestText_ApiRead"] = 1,
    };

    private static Dictionary<string, string> Resw()
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

    private static int HighestPlaceholder(string text)
    {
        var indexes = Regex.Matches(text, @"\{(\d+)\}").Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();
        return indexes.Count == 0 ? 0 : indexes.Max() + 1;
    }

    [Fact]
    public void Every_Budget_key_has_the_placeholders_the_code_passes_arguments_for()
    {
        var budgetKeys = Resw().Where(p => p.Key.StartsWith("Budget", StringComparison.Ordinal)).ToList();
        budgetKeys.Count.ShouldBeGreaterThan(25, "vacuity: the scan must see the whole budget group");

        foreach (var (key, text) in budgetKeys)
        {
            HighestPlaceholder(text).ShouldBe(PassedArguments.GetValueOrDefault(key, 0), $"{key}: its highest {{n}} must match the arguments the code passes (a key the code formats must be listed in PassedArguments)");
        }

        // The request texts are formatted with (project, amount) by CopyRequest: any request key that uses fewer must be listed as an extra on purpose.
        foreach (var (key, used) in PassedArguments.Where(p => p.Key.StartsWith("BudgetRequestText", StringComparison.Ordinal)))
        {
            (used + ExtraArgumentsPassed.GetValueOrDefault(key, 0)).ShouldBe(2, $"{key}: CopyRequest passes two arguments, so a key using fewer must be listed in ExtraArgumentsPassed");
        }

        foreach (var key in PassedArguments.Keys)
        {
            budgetKeys.ShouldContain(p => p.Key == key, $"PassedArguments lists {key}, which is not in Resources.resw");
        }
    }

    [Fact]
    public void The_threshold_joiner_keeps_its_space_and_the_phrase_keys_carry_no_em_dash()
    {
        var resw = Resw();
        resw["BudgetThresholdsJoiner"].ShouldBe(", ");
        resw.Where(p => p.Key.StartsWith("Budget", StringComparison.Ordinal)).ShouldAllBe(p => !p.Value.Contains('—'));
    }

    private sealed class RealStrings(Dictionary<string, string> resw) : IStringResourceProvider
    {
        public string GetString(string key) => resw.TryGetValue(key, out var value) ? value : throw new KeyNotFoundException(key);
    }

    private static (BudgetSettingsViewModel Vm, IBudgetService Budgets, IShellLauncher Shell) Open()
    {
        var budgets = Substitute.For<IBudgetService>();
        var account = Substitute.For<IGcpAccount>();
        account.SelectedProjectId.Returns("my-lab");
        budgets.ConsoleLinkAsync("my-lab", Arg.Any<CancellationToken>()).Returns("https://console.cloud.google.com/billing/AAA/budgets");
        var shell = Substitute.For<IShellLauncher>();
        shell.CopyText(Arg.Any<string>()).Returns(true);
        return (new BudgetSettingsViewModel(budgets, account, shell, new RealStrings(Resw())), budgets, shell);
    }

    private static BudgetInfo Budget(decimal amount, bool known, params double[] percents)
        => new("billingAccounts/AAA/budgets/b-1", "DNA Entropy Graph inst-a my-lab", "billingAccounts/AAA", amount, percents, known);

    [Theory]
    [InlineData("50", "50%")]
    [InlineData("50,90", "50% and 90%")]
    [InlineData("50,90,100", "50%, 90% and 100%")]
    public async Task The_status_and_saved_sentences_come_out_whole_from_the_real_copy(string percents, string phrase)
    {
        var thresholds = percents.Split(',').Select(p => double.Parse(p, CultureInfo.InvariantCulture)).ToArray();
        var (vm, budgets, _) = Open();
        budgets.GetAsync("my-lab", Arg.Any<CancellationToken>()).Returns(Budget(75m, true, thresholds));

        await vm.RefreshCommand.ExecuteAsync(null);

        vm.StatusText.ShouldContain(phrase);
        vm.StatusText.ShouldContain(75m.ToString("0.##", CultureInfo.CurrentCulture));
        vm.StatusText.ShouldNotContain("{");

        budgets.GetAsync("my-lab", Arg.Any<CancellationToken>()).Returns(Budget(0m, false, thresholds));
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.StatusText.ShouldContain(phrase);
        vm.StatusText.ShouldNotContain("{");

        budgets.SaveAsync("my-lab", 75m, Arg.Any<CancellationToken>()).Returns(Budget(75m, true, thresholds));
        vm.AmountText = "75";
        await vm.SaveCommand.ExecuteAsync(null);
        vm.StatusText.ShouldContain(phrase);
        vm.StatusText.ShouldContain(75m.ToString("0.##", CultureInfo.CurrentCulture));
        vm.StatusText.ShouldNotContain("{");
    }

    [Fact]
    public async Task The_copied_requests_come_out_whole_naming_the_project_and_the_amount()
    {
        var (vm, budgets, shell) = Open();
        vm.AmountText = "75";
        vm.CopyRequestCommand.Execute(null);
        shell.Received().CopyText(Arg.Is<string>(t => t.Contains("my-lab", StringComparison.Ordinal) && t.Contains("75", StringComparison.Ordinal) && !t.Contains('{')));

        budgets.SaveAsync("my-lab", 75m, Arg.Any<CancellationToken>()).Returns<Task<BudgetInfo>>(_ => throw new CloudOperationException(new CloudError(null, 403, "x"), CloudErrorKind.OrgPolicy));
        await vm.SaveCommand.ExecuteAsync(null);
        shell.ClearReceivedCalls();
        vm.CopyRequestCommand.Execute(null);
        shell.Received().CopyText(Arg.Is<string>(t => t.Contains("my-lab", StringComparison.Ordinal) && t.Contains("75", StringComparison.Ordinal) && !t.Contains('{')));
    }
}
