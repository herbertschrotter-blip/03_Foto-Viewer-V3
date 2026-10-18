using System;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using PhotoViewerV3.Domain.Enums;
using PhotoViewerV3.Spike02.Data;

namespace PhotoViewerV3.Spike02.ViewModels;

/// <summary>
/// Beobachtbares Grid-Item. Kapselt die unveränderlichen Datei-Daten aus
/// <see cref="GalleryFileDto"/> und trägt zusätzlich das nachträglich vom
/// ThumbnailService gelieferte Vorschaubild (<see cref="Thumbnail"/>).
/// </summary>
public sealed partial class GalleryItem : ObservableObject
{
    public GalleryItem(GalleryFileDto dto)
    {
        Id = dto.Id;
        Name = dto.Name;
        Extension = dto.Extension;
        SizeDisplay = dto.SizeDisplay;
        IsVideo = dto.IsVideo;
        IsFavorite = dto.IsFavorite;
        MediaType = (MediaType)dto.MediaType;
        SourcePath = Path.Combine(dto.FolderPath, dto.Name);
        SourceModifiedAtUtc = DateTimeOffset.FromUnixTimeSeconds(dto.ModifiedAtUnix).UtcDateTime;
    }

    public int Id { get; }
    public string Name { get; }
    public string Extension { get; }
    public string SizeDisplay { get; }
    public bool IsVideo { get; }
    public bool IsFavorite { get; }
    public MediaType MediaType { get; }
    public string SourcePath { get; }
    public DateTime SourceModifiedAtUtc { get; }

    /// <summary>Vorschaubild — null, solange das Thumbnail noch generiert wird.</summary>
    [ObservableProperty] private ImageSource? _thumbnail;
}
