using System;
using CommunityToolkit.Mvvm.ComponentModel;
using PhotoViewerV3.Domain.Enums;

namespace PhotoViewerV3.Lightbox;

/// <summary>
/// Beobachtbarer Wrapper um einen <see cref="LightboxEntry"/>. Der Vertrag (Record) bleibt
/// unveränderlich, die Lightbox hält aber pro Eintrag veränderliche Anzeige-Daten
/// (Favorit, Bewertung, Pfad nach Move/Rename), die per FilesChangedMessage nachgeführt werden.
/// </summary>
public sealed partial class LightboxItem : ObservableObject
{
    public LightboxItem(LightboxEntry entry)
    {
        FileId = entry.FileId;
        MediaType = entry.MediaType;
        _path = entry.Path;
        _name = entry.Name;
        _isFavorite = entry.IsFavorite;
        _rating = entry.Rating;
        _size = entry.Size;
        _modifiedAtUtc = entry.ModifiedAtUtc;
    }

    public int FileId { get; }

    public MediaType MediaType { get; }

    public bool IsVideo => MediaType == MediaType.Video;

    public bool IsImage => MediaType == MediaType.Image;

    [ObservableProperty] private string _path;
    [ObservableProperty] private string _name;
    [ObservableProperty] private bool _isFavorite;
    [ObservableProperty] private byte? _rating;
    [ObservableProperty] private long _size;
    [ObservableProperty] private DateTime _modifiedAtUtc;
}

/// <summary>Eine Zeile im Info-Panel (Label links, Wert rechts).</summary>
public sealed record LightboxInfoRow(string Label, string Value);
