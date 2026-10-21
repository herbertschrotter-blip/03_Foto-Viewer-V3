using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using Lumora.Domain.Enums;

namespace Lumora.Lightbox;

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

    /// <summary>Archiv (ADR-0022): die Lightbox zeigt die Inhaltsliste statt eines Bildes.</summary>
    public bool IsArchive => MediaType == MediaType.Archive;

    [ObservableProperty] private string _path;
    [ObservableProperty] private string _name;
    [ObservableProperty] private bool _isFavorite;
    [ObservableProperty] private byte? _rating;
    [ObservableProperty] private long _size;
    [ObservableProperty] private DateTime _modifiedAtUtc;
}

/// <summary>Eine Zeile im Info-Panel (Label links, Wert rechts).</summary>
public sealed record LightboxInfoRow(string Label, string Value);

/// <summary>Eine Zeile der Archiv-Inhaltsansicht (ADR-0022).</summary>
public sealed class ArchiveEntryRow
{
    // Nur fürs Symbol (Bild vs. Video); ob ein Eintrag Medium ist, entscheidet der Service über das Register.
    private static readonly HashSet<string> VideoExtensions = new(Lumora.Services.Media.MediaFormatRegistry.DefaultVideoExtensions, StringComparer.OrdinalIgnoreCase);

    public ArchiveEntryRow(Lumora.Services.Archive.ArchiveEntryInfo entry)
    {
        Path = entry.Path;
        IsMedia = entry.IsMedia;
        IsArchive = entry.IsArchive;
        SizeText = Shell.Formatting.Bytes(entry.Size);
        CompressedText = entry.CompressedSize > 0 ? Shell.Formatting.Bytes(entry.CompressedSize) : "–";
        ModifiedText = entry.ModifiedAtUtc?.ToLocalTime().ToString("dd.MM.yyyy HH:mm", System.Globalization.CultureInfo.CurrentCulture) ?? "–";
        Symbol = entry.IsArchive
            ? Wpf.Ui.Controls.SymbolRegular.Archive24
            : entry.IsMedia
                ? (VideoExtensions.Contains(System.IO.Path.GetExtension(entry.Name)) ? Wpf.Ui.Controls.SymbolRegular.Video24 : Wpf.Ui.Controls.SymbolRegular.Image24)
                : Wpf.Ui.Controls.SymbolRegular.Document24;
    }

    public string Path { get; }
    public bool IsMedia { get; }
    public bool IsArchive { get; }
    public string SizeText { get; }
    public string CompressedText { get; }
    public string ModifiedText { get; }
    public Wpf.Ui.Controls.SymbolRegular Symbol { get; }
}
