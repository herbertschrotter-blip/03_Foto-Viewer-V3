using CommunityToolkit.Mvvm.ComponentModel;

namespace Lumora.Gallery.Motifs;

/// <summary>
/// Eintrag im Motiv-Filter (ADR-0052): Begriff mit Anzahl Dateien im Bereich, Häkchen = gehört zum Filter. Der Zähler
/// wird während des Hintergrund-Index an Ort und Stelle nachgezogen (kein Neuaufbau der Liste bei offenem Flyout).
/// </summary>
public sealed partial class MotifFilterEntry : ObservableObject
{
    public MotifFilterEntry(int termId, string name, int fileCount)
    {
        TermId = termId;
        Name = name;
        _fileCount = fileCount;
    }

    public int TermId { get; }

    public string Name { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText))]
    private int _fileCount;

    public string CountText => FileCount.ToString("N0");

    [ObservableProperty] private bool _isSelected;
}
