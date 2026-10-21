using CommunityToolkit.Mvvm.ComponentModel;

namespace PhotoViewerV3.Gallery.People;

/// <summary>
/// Eintrag im Personen-Filter der Galerie (0.2.38): Person mit Anzahl Dateien der aktiven Bibliothek, auf denen sie
/// bestätigt ist; Häkchen = Person gehört zum Filter. Namespace bewusst „People“, damit er weder die Entität
/// <c>Person</c> noch die Personen-Seite verdeckt.
/// </summary>
public sealed partial class PersonFilterEntry : ObservableObject
{
    public PersonFilterEntry(int personId, string name, int fileCount)
    {
        PersonId = personId;
        Name = name;
        FileCount = fileCount;
    }

    public int PersonId { get; }

    public string Name { get; }

    public int FileCount { get; }

    public string CountText => FileCount.ToString("N0");

    [ObservableProperty] private bool _isSelected;
}
