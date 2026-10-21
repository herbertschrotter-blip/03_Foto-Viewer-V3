using CommunityToolkit.Mvvm.ComponentModel;

namespace Lumora.Albums;

/// <summary>Eintrag im Galerie-Filter „Album“ und im Menü „Zu Album hinzufügen“ (ADR-0054).</summary>
public sealed partial class AlbumFilterEntry : ObservableObject
{
    public AlbumFilterEntry(int albumId, string name, int? fileCount, bool isSmart)
    {
        AlbumId = albumId;
        Name = name;
        FileCount = fileCount;
        IsSmart = isSmart;
    }

    public int AlbumId { get; }

    public string Name { get; }

    public int? FileCount { get; }

    public bool IsSmart { get; }

    public string CountText => FileCount is { } n ? n.ToString("N0") : string.Empty;

    /// <summary>Zweite Zeile im Flyout: intelligente Alben laden ihren Filter statt einer Bedingung.</summary>
    public string KindText => IsSmart ? "intelligent · lädt den Filter" : string.Empty;

    [ObservableProperty] private bool _isSelected;
}
