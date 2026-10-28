using CommunityToolkit.Mvvm.Input;

namespace DnaEntropyGraph.Presentation.ViewModels;

/// <summary>
/// One computer (VM) on the Cloud page. A computer of this installation has Stop, Start and Delete; one that another installation created in the same project is
/// listed read-only (<see cref="IsAnotherComputer"/>, no commands): Hard Rule 9, two people may share one Google account and neither may end the other's run.
/// </summary>
public sealed class CloudVmRow
{
    public CloudVmRow(string name, string zone, string statusText, string costText, string tagText, bool isAnotherComputer)
    {
        Name = name;
        Zone = zone;
        StatusText = statusText;
        CostText = costText;
        TagText = tagText;
        IsAnotherComputer = isAnotherComputer;
    }

    public string Name { get; }

    public string Zone { get; }

    public string StatusText { get; }

    /// <summary>What it costs now ("up to about $0.85 per hour while it runs", "about $0.49 per day for its disk"); empty when the price list could not be read.</summary>
    public string CostText { get; }

    /// <summary>"Created on another computer" for a read-only row, else empty.</summary>
    public string TagText { get; }

    public bool IsAnotherComputer { get; }

    public bool HasTag => TagText.Length > 0;

    public bool HasCost => CostText.Length > 0;

    public bool HasActions => !IsAnotherComputer;

    public IAsyncRelayCommand? StopCommand { get; init; }

    public IAsyncRelayCommand? StartCommand { get; init; }

    public IAsyncRelayCommand? DeleteCommand { get; init; }
}

/// <summary>One disk of this installation on the Cloud page (a stopped computer's disk keeps costing money until it is deleted).</summary>
public sealed record CloudDiskRow(string Name, string Zone, string DetailText, string CostText)
{
    public bool HasCost => CostText.Length > 0;
}
