using System;
using System.Windows.Media;
using Lumora.Gallery.Controls;
using Lumora.Gallery.Data;

namespace Lumora.Gallery.ViewModels;

/// <summary>
/// Seitenverhältnis für die Collage (ADR-0044): aus <c>Files.Width/Height</c> (seit 0.2.45 beim Erzeugen des
/// Vorschaubilds nach EXIF-Drehung gefüllt); fehlen sie oder sind 0 × 0, aus dem geladenen Vorschaubild; sonst und bei
/// Archiven 4:3. Ändert es sich (Maße beim Neuladen nachgetragen, erstes Vorschaubild ohne Maße), meldet das Item
/// <see cref="LayoutAspectRatio"/> — die Galerie lässt die Collage daraufhin neu umbrechen.
/// </summary>
public sealed partial class GalleryItem : IJustifiedLayoutItem
{
    private int _mediaWidth;
    private int _mediaHeight;
    private double _layoutAspectRatio = JustifiedRowLayout.DefaultAspectRatio;

    /// <summary>Breite des Originals nach EXIF-Drehung in Pixeln; null, wenn unbekannt.</summary>
    public int? MediaWidth => _mediaWidth > 0 && _mediaHeight > 0 ? _mediaWidth : null;

    /// <summary>Höhe des Originals nach EXIF-Drehung in Pixeln; null, wenn unbekannt.</summary>
    public int? MediaHeight => _mediaWidth > 0 && _mediaHeight > 0 ? _mediaHeight : null;

    /// <inheritdoc />
    public double LayoutAspectRatio => _layoutAspectRatio;

    /// <summary>Seitenverhältnis aus Maßen, sonst aus dem Vorschaubild, sonst (und für Archive) 4:3.</summary>
    internal static double ComputeAspectRatio(bool isArchive, int width, int height, ImageSource? thumbnail)
    {
        if (isArchive)
        {
            return JustifiedRowLayout.DefaultAspectRatio;
        }

        if (width > 0 && height > 0)
        {
            return (double)width / height;
        }

        if (thumbnail is not null && thumbnail.Width > 0 && thumbnail.Height > 0)
        {
            return thumbnail.Width / thumbnail.Height;
        }

        return JustifiedRowLayout.DefaultAspectRatio;
    }

    /// <summary>Maße beim Neuladen der Seite übernehmen (das Item wird bei unveränderter Datei wiederverwendet).</summary>
    internal void UpdateMediaSize(GalleryFileDto dto)
    {
        var width = dto.Width ?? 0;
        var height = dto.Height ?? 0;
        if (width == _mediaWidth && height == _mediaHeight)
        {
            return;
        }

        _mediaWidth = width;
        _mediaHeight = height;
        RefreshLayoutAspectRatio();
    }

    private void InitMediaSize(GalleryFileDto dto)
    {
        _mediaWidth = dto.Width ?? 0;
        _mediaHeight = dto.Height ?? 0;
        _layoutAspectRatio = ComputeAspectRatio(dto.IsArchive, _mediaWidth, _mediaHeight, null);
    }

    partial void OnThumbnailChanged(ImageSource? value) => RefreshLayoutAspectRatio();

    private void RefreshLayoutAspectRatio()
    {
        var aspect = ComputeAspectRatio(IsArchive, _mediaWidth, _mediaHeight, Thumbnail);
        if (Math.Abs(aspect - _layoutAspectRatio) < 0.005)
        {
            return;
        }

        _layoutAspectRatio = aspect;
        OnPropertyChanged(nameof(LayoutAspectRatio));
    }
}
