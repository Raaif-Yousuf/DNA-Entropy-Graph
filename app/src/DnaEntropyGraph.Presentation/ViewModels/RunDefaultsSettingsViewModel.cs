using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Presentation.Messaging;
using DnaEntropyGraph.Presentation.Services;

namespace DnaEntropyGraph.Presentation.ViewModels;

/// <summary>
/// The Settings "Run defaults" group (issue #640): the output files a new run starts with, per input kind. It reads and
/// writes <see cref="OutputSelectionStore"/>, the same store the New run page loads when it opens and saves when a run starts,
/// so there is no second copy of any default. Only what New run offers is listed (the folder and name template are not New
/// run options today, so they are not invented here).
/// </summary>
public sealed partial class RunDefaultsSettingsViewModel : ObservableObject
{
    private readonly OutputSelectionStore _store;
    private readonly IStringResourceProvider _strings;
    private readonly IDispatcher _dispatcher;
    private readonly Dictionary<OutputInputKinds, OutputFileKinds> _saved = [];
    private bool _applying;

    [ObservableProperty]
    private string _genBankStatus = string.Empty;

    [ObservableProperty]
    private string _sequenceStatus = string.Empty;

    public RunDefaultsSettingsViewModel(OutputSelectionStore store, IStringResourceProvider strings, IDispatcher dispatcher, IMessenger? messenger = null)
    {
        _store = store;
        _strings = strings;
        _dispatcher = dispatcher;
        Build(GenBankChoices, OutputInputKinds.GenBank);
        Build(SequenceChoices, OutputInputKinds.Sequence);
        messenger?.Register<RunDefaultsSettingsViewModel, SettingsResetMessage>(this, static (vm, _) => vm.Reload());
    }

    /// <summary>The boxes for a GenBank input.</summary>
    public ObservableCollection<OutputChoiceItem> GenBankChoices { get; } = [];

    /// <summary>The boxes for a FASTA file or a pasted sequence.</summary>
    public ObservableCollection<OutputChoiceItem> SequenceChoices { get; } = [];

    /// <summary>Re-reads both lists after Reset all settings: the same boxes, set in place so the page's bindings stay attached, and nothing is saved.</summary>
    private void Reload()
    {
        _applying = true;
        try
        {
            Refresh(GenBankChoices, OutputInputKinds.GenBank);
            Refresh(SequenceChoices, OutputInputKinds.Sequence);
            GenBankStatus = string.Empty;
            SequenceStatus = string.Empty;
        }
        finally
        {
            _applying = false;
        }
    }

    private void Refresh(ObservableCollection<OutputChoiceItem> list, OutputInputKinds group)
    {
        var selection = LoadOrDefaults(group);
        _saved[group] = selection;
        foreach (var item in list)
        {
            item.IsChecked = selection.HasFlag(item.Entry.Kind);
        }
    }

    private OutputFileKinds LoadOrDefaults(OutputInputKinds group)
    {
        try
        {
            return _store.Load(group);
        }
        catch (SettingsUnavailableException ex)
        {
            System.Diagnostics.Trace.TraceWarning($"settings_unavailable while reading run defaults: {ex.GetType().Name}");
            return OutputCatalog.Defaults;
        }
    }

    private void Build(ObservableCollection<OutputChoiceItem> list, OutputInputKinds group)
    {
        OutputFileKinds selection;
        try
        {
            selection = _store.Load(group);
        }
        catch (SettingsUnavailableException ex)
        {
            System.Diagnostics.Trace.TraceWarning($"settings_unavailable while reading run defaults: {ex.GetType().Name}");
            selection = OutputCatalog.Defaults;
        }

        _saved[group] = selection;

        foreach (var entry in OutputCatalog.For(group))
        {
            list.Add(new OutputChoiceItem(entry, _strings.GetString(entry.LabelKey), selection.HasFlag(entry.Kind), item => OnChanged(group, list, item)));
        }
    }

    private void OnChanged(OutputInputKinds group, ObservableCollection<OutputChoiceItem> list, OutputChoiceItem item)
    {
        if (_applying)
        {
            return;
        }

        _applying = true;
        try
        {
            // The worker writes one track format per run: the bedGraph and WIG are alternatives (OutputCatalog.Normalize).
            if (item.IsChecked && item.Entry.Kind is OutputFileKinds.BedGraph or OutputFileKinds.Wig)
            {
                var other = item.Entry.Kind == OutputFileKinds.Wig ? OutputFileKinds.BedGraph : OutputFileKinds.Wig;
                list.FirstOrDefault(c => c.Entry.Kind == other)?.IsChecked = false;
            }

            if (!list.Any(c => c.IsChecked))
            {
                // A run must return at least one file: put the box back instead of saving nothing. The put-back is queued, not
                // done inside this change notification, because a TwoWay x:Bind may not repaint a value set from inside the
                // notification it is still handling (THEORY (unverified), see docs/ToTest.md).
                Revert(() => item.IsChecked = true);
                SetStatus(group, _strings.GetString("RunDefaultsNoneChosen"));
                return;
            }

            var selection = list.Where(c => c.IsChecked).Aggregate(OutputFileKinds.None, (all, c) => all | c.Entry.Kind);
            try
            {
                _store.Save(group, selection);
                _saved[group] = selection;
                SetStatus(group, string.Empty);
            }
            catch (SettingsUnavailableException ex)
            {
                System.Diagnostics.Trace.TraceWarning($"settings_unavailable while saving run defaults: {ex.GetType().Name}");
                // Nothing was saved, so the boxes go back to what is saved: the screen never shows a default that is not kept.
                var saved = _saved[group];
                Revert(() =>
                {
                    foreach (var choice in list)
                    {
                        choice.IsChecked = saved.HasFlag(choice.Entry.Kind);
                    }
                });
                SetStatus(group, _strings.GetString("RunDefaultsNotSaved"));
            }
        }
        finally
        {
            _applying = false;
        }
    }

    private void Revert(Action put)
    {
        _dispatcher.Enqueue(() =>
        {
            _applying = true;
            try
            {
                put();
            }
            finally
            {
                _applying = false;
            }
        });
    }

    private void SetStatus(OutputInputKinds group, string text)
    {
        if (group == OutputInputKinds.GenBank)
        {
            GenBankStatus = text;
        }
        else
        {
            SequenceStatus = text;
        }
    }
}
