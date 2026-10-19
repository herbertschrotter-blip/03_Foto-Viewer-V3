using System;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using PhotoViewerV3.Domain.Enums;
using PhotoViewerV3.Gallery.Data;

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
        MediaType = (MediaType)dto.MediaType;
        FolderPath = dto.FolderPath;
        SourcePath = Path.Combine(dto.FolderPath, dto.Name);
        ModifiedAtUnix = dto.ModifiedAtUnix;
        SourceModifiedAtUtc = DateTimeOffset.FromUnixTimeSeconds(dto.ModifiedAtUnix).UtcDateTime;
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
    public MediaType MediaType { get; }
    public string FolderPath { get; }
    public string SourcePath { get; }
    public long ModifiedAtUnix { get; }
    public DateTime SourceModifiedAtUtc { get; }

    [ObservableProperty] private bool _isFavorite;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRating))]
    private byte? _rating;

    [ObservableProperty] private bool _missing;

    /// <summary>Auswahlzustand — zweiseitig an ListBoxItem.IsSelected gebunden.</summary>
    [ObservableProperty] private bool _isSelected;

    /// <summary>Vorschaubild — null, solange das Thumbnail noch generiert wird.</summary>
    [ObservableProperty] private ImageSource? _thumbnail;

    public bool HasRating => Rating is > 0;

    /// <summary>True, wenn die unveränderlichen Datei-Daten mit dem DTO übereinstimmen (Instanz wiederverwendbar).</summary>
    public bool CoreMatches(GalleryFileDto dto)
        => Id == dto.Id
           && FolderId == dto.FolderId
           && Size == dto.Size
           && ModifiedAtUnix == dto.ModifiedAtUnix
           && MediaType == (MediaType)dto.MediaType
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
