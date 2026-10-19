using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using PhotoViewerV3.Domain.Enums;
using PhotoViewerV3.Gallery.Data;
using PhotoViewerV3.Services.Tags;

namespace PhotoViewerV3.Gallery.ViewModels;

/// <summary>
/// Beobachtbares Grid-Item. Kapselt die unveränderlichen Datei-Daten aus
/// <see cref="GalleryFileDto"/>; Benutzer-Metadaten (Favorit, Bewertung, Missing) und die
/// Auswahl sind beobachtbar, damit FilesChangedMessage(Metadata) in place aktualisieren kann.
/// Das Vorschaubild (<see cref="Thumbnail"/>) liefert der ThumbnailService nachträglich.
/// </summary>
public sealed partial class GalleryItem : ObservableObject
{
    public GalleryItem(GalleryFileDto dto)
    {
        Id = dto.Id;
        FolderId = dto.FolderId;
        Name = dto.Name;
        Extension = dto.Extension;
        Size = dto.Size;
        SizeDisplay = dto.SizeDisplay;
        IsVideo = dto.IsVideo;
        IsArchive = dto.IsArchive;
        MediaType = (MediaType)dto.MediaType;
        FolderPath = dto.FolderPath;
        SourcePath = Path.Combine(dto.FolderPath, dto.Name);
        ModifiedAtUnix = dto.ModifiedAtUnix;
        SourceModifiedAtUtc = DateTimeOffset.FromUnixTimeSeconds(dto.ModifiedAtUnix).UtcDateTime;
        IsInbox = dto.IsInbox;
        IsArchived = dto.RootRole == 1;
        _isFavorite = dto.IsFavorite;
        _rating = dto.Rating;
        _missing = dto.Missing;
    }

    public int Id { get; }
    public int FolderId { get; }
    public string Name { get; }
    public string Extension { get; }
    public long Size { get; }
    public string SizeDisplay { get; }
    public bool IsVideo { get; }

    /// <summary>Archiv-Kachel (ADR-0022): kein Thumbnail, Icon + Endung + Größe; Doppelklick öffnet die Inhaltsansicht.</summary>
    public bool IsArchive { get; }

    /// <summary>Endung ohne Punkt in Großbuchstaben für das Archiv-Badge („ZIP“).</summary>
    public string ArchiveBadge => Extension.TrimStart('.').ToUpperInvariant();
    public MediaType MediaType { get; }
    public string FolderPath { get; }
    public string SourcePath { get; }
    public long ModifiedAtUnix { get; }
    public DateTime SourceModifiedAtUtc { get; }

    /// <summary>Datei liegt unter einer Eingangs-Wurzel (Badge „Eingang“, ADR-0020).</summary>
    public bool IsInbox { get; }

    /// <summary>Datei liegt unter der Archiv-Wurzel (ADR-0020); Zeilen ohne bekannte Wurzel sind weder Eingang noch Archiv.</summary>
    public bool IsArchived { get; }

    /// <summary>Spalte „Status“ der Listenansicht: „fehlt“ vor „Eingang“/„Archiv“, leer bei unbekannter Wurzel.</summary>
    public string StatusText => Missing ? "fehlt" : IsInbox ? "Eingang" : IsArchived ? "Archiv" : string.Empty;

    [ObservableProperty] private bool _isFavorite;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRating))]
    private byte? _rating;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private bool _missing;

    /// <summary>Auswahlzustand — zweiseitig an ListBoxItem.IsSelected gebunden.</summary>
    [ObservableProperty] private bool _isSelected;

    /// <summary>Vorschaubild — null, solange das Thumbnail noch generiert wird.</summary>
    [ObservableProperty] private ImageSource? _thumbnail;

    /// <summary>Zugewiesene Tags in Anzeigereihenfolge (ADR-0021); die Liste wird als Ganzes ersetzt.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTags), nameof(TagsText))]
    private IReadOnlyList<TagInfo> _tags = Array.Empty<TagInfo>();

    public bool HasRating => Rating is > 0;

    public bool HasTags => Tags.Count > 0;

    /// <summary>Tag-Namen durch Komma (Tooltip, Spalte „Tags“ und deren Sortierung innerhalb der Page).</summary>
    public string TagsText => string.Join(", ", Tags.Select(t => t.Name));

    public bool HasTag(int tagId)
    {
        foreach (var tag in Tags)
        {
            if (tag.Id == tagId)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True, wenn die unveränderlichen Datei-Daten mit dem DTO übereinstimmen (Instanz wiederverwendbar).</summary>
    public bool CoreMatches(GalleryFileDto dto)
        => Id == dto.Id
           && FolderId == dto.FolderId
           && Size == dto.Size
           && ModifiedAtUnix == dto.ModifiedAtUnix
           && MediaType == (MediaType)dto.MediaType
           && IsInbox == dto.IsInbox
           && IsArchived == (dto.RootRole == 1)
           && string.Equals(Name, dto.Name, StringComparison.Ordinal)
           && string.Equals(Extension, dto.Extension, StringComparison.Ordinal)
           && string.Equals(FolderPath, dto.FolderPath, StringComparison.Ordinal);

    public void UpdateMetadata(bool isFavorite, byte? rating, bool missing)
    {
        IsFavorite = isFavorite;
        Rating = rating;
        Missing = missing;
    }
}
