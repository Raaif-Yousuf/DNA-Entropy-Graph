using System.Windows.Input;

namespace DnaEntropyGraph.Presentation.ViewModels;

/// <summary>One contig's row in the stats header. Every text is the worker's own number, formatted for display.</summary>
public sealed record ResultsContigRow(string Name, string LengthText, string MeanText, string MinText, string MaxText, string DirectionText);

/// <summary>One summary file's numbers: a headline and its contigs. <see cref="Title"/> is empty when the run has only one summary.</summary>
public sealed record ResultsStatsGroup(string Title, string Headline, IReadOnlyList<ResultsContigRow> Contigs)
{
    public bool HasTitle => Title.Length > 0;
}

/// <summary>One file in the run's output folder, with the three things the page can do to it.</summary>
public sealed record ResultsFileItem(string Name, string SizeText, ICommand OpenCommand, ICommand ShowInFolderCommand, ICommand CopyPathCommand);
