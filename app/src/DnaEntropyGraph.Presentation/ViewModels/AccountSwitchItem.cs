using CommunityToolkit.Mvvm.Input;

namespace DnaEntropyGraph.Presentation.ViewModels;

/// <summary>One "Switch to b@lab.org" button on the Runs page (issue #112): the command lives on the item so the list needs no code-behind.</summary>
public sealed record AccountSwitchItem(string Label, IAsyncRelayCommand SwitchCommand);
