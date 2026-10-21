using System.Windows;

namespace Lumora.Lightbox.Controls;

/// <summary>
/// Ebene über dem Bild eines <see cref="ZoomPanControl"/> (0.2.38, Gesichtsrahmen): bekommt nach jedem Zoom, Schwenk
/// und Bildwechsel das sichtbare Bildrechteck in Koordinaten des Controls (<see cref="Rect.Empty"/> ohne Bild).
/// </summary>
public interface IZoomPanOverlay
{
    void UpdateImageRect(Rect imageRect);
}
