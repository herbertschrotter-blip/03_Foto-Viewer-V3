using CommunityToolkit.Mvvm.ComponentModel;

namespace PhotoViewerV3.Gallery.People;

/// <summary>
/// Eintrag im Abschnitt „Beziehung“ des Personen-Flyouts (0.2.47, ADR-0045): Beziehung mit Anzahl Dateien der aktiven
/// Bibliothek, auf denen eine ihrer Personen bestätigt ist; Häkchen = Beziehung gehört zum Filter.
/// </summary>
public sealed partial class RelationFilterEntry : ObservableObject
{
    public RelationFilterEntry(int relationId, string name, int fileCount)
    {
        RelationId = relationId;
        Name = name;
        FileCount = fileCount;
    }

    public int RelationId { get; }

    public string Name { get; }

    public int FileCount { get; }

    public string CountText => FileCount.ToString("N0");

    [ObservableProperty] private bool _isSelected;
}
