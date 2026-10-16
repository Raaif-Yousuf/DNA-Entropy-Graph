using DnaEntropyGraph.Core.Cloud;

namespace DnaEntropyGraph.Cloud;

// The Cloud Billing budget (issues #148 and #222): IBudgetGateway with scripted failures. A partial file, like the project
// catalogue, so the budget scenarios do not grow the runner fake any further. Budgets live on the billing account and are found by
// display name, exactly as the real gateway does (Hard Rule 9), so two installations on one account never see each other's.
public sealed partial class FakeGcp : IBudgetGateway
{
    private readonly object _budgetGate = new();
    private readonly Dictionary<string, FakeBudget> _budgets = new(StringComparer.Ordinal);
    private int _budgetFindCalls;
    private int _budgetUpsertCalls;
    private int _budgetSequence;
    private bool _budgetPermissionDenied;

    private sealed record FakeBudget(string Name, string DisplayName, string BillingAccountId, string ProjectId, double AmountUsd);

    /// <summary>Every budget the fake holds, in no particular order.</summary>
    public IReadOnlyList<BudgetInfo> Budgets
    {
        get
        {
            lock (_budgetGate)
            {
                return _budgets.Values.Select(ToInfo).ToList();
            }
        }
    }

    /// <summary>How many times a list reached Google (a page that looks while the Budgets API is off, or on every keystroke, shows here).</summary>
    public int BudgetFindCalls => Volatile.Read(ref _budgetFindCalls);

    /// <summary>How many times a create or update reached Google (a refusal before any call leaves this at zero).</summary>
    public int BudgetUpsertCalls => Volatile.Read(ref _budgetUpsertCalls);

    /// <summary>The project a stored budget watches; null for an unknown name.</summary>
    public string? BudgetProjectOf(string budgetName)
    {
        lock (_budgetGate)
        {
            return _budgets.GetValueOrDefault(budgetName)?.ProjectId;
        }
    }

    /// <summary>The user holds no billing role on the account (a project owner is often not a billing administrator): every budget call throws <see cref="CloudErrorKind.Permission"/>.</summary>
    public FakeGcp WithBudgetPermissionDenied()
    {
        _budgetPermissionDenied = true;
        return this;
    }

    public Task<BudgetInfo?> FindAsync(string billingAccountId, string installationId, CancellationToken cancellationToken)
    {
        ThrowIfScriptedTransient();
        ThrowIfCatalogNotConnected();
        Interlocked.Increment(ref _budgetFindCalls);
        ThrowIfBudgetDenied();
        lock (_budgetGate)
        {
            var found = _budgets.Values.FirstOrDefault(b => b.BillingAccountId == billingAccountId && BudgetNaming.IsOurs(b.DisplayName, installationId));
            return Task.FromResult(found is null ? null : ToInfo(found));
        }
    }

    public Task<BudgetInfo> UpsertAsync(BudgetSpec spec, CancellationToken cancellationToken)
    {
        ThrowIfScriptedTransient();
        ThrowIfCatalogNotConnected();
        Interlocked.Increment(ref _budgetUpsertCalls);
        ThrowIfBudgetDenied();
        lock (_budgetGate)
        {
            var existing = _budgets.Values.FirstOrDefault(b => b.BillingAccountId == spec.BillingAccountId && BudgetNaming.IsOurs(b.DisplayName, spec.InstallationId));
            var name = existing?.Name ?? $"{spec.BillingAccountId}/budgets/fake-{++_budgetSequence}";
            var stored = new FakeBudget(name, BudgetNaming.DisplayName(spec.InstallationId), spec.BillingAccountId, spec.ProjectId, spec.AmountUsd);
            _budgets[name] = stored;
            return Task.FromResult(ToInfo(stored));
        }
    }

    public Task DeleteAsync(string budgetName, CancellationToken cancellationToken)
    {
        ThrowIfScriptedTransient();
        ThrowIfCatalogNotConnected();
        ThrowIfBudgetDenied();
        lock (_budgetGate)
        {
            // Already gone is not an error (the interface says so): a replay after a dropped connection must succeed.
            _budgets.Remove(budgetName);
        }

        return Task.CompletedTask;
    }

    private void ThrowIfBudgetDenied()
    {
        if (_budgetPermissionDenied)
        {
            throw Build(CloudErrorKind.Permission, null, 403, "The caller does not have permission billing.budgets.create on the billing account.");
        }
    }

    private static BudgetInfo ToInfo(FakeBudget b) => new(b.Name, b.DisplayName, b.BillingAccountId, b.AmountUsd, BudgetNaming.ThresholdPercents);
}
