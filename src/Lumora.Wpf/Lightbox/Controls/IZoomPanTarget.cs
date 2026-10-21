namespace Lumora.Lightbox.Controls;

/// <summary>
/// Zoom-Befehle, die das ViewModel an die Bildanzeige weiterreicht (Toolbar-Buttons, Tasten).
/// Der Zoom-Zustand selbst ist View-Mechanik und lebt im <see cref="ZoomPanControl"/>.
/// </summary>
public interface IZoomPanTarget
{
    void ZoomIn();

    void ZoomOut();

    /// <summary>Einpassen (ohne Hochskalieren über 100 %).</summary>
    void ZoomToFit();

    /// <summary>100 % — ein Bildpixel entspricht einem Gerätepixel.</summary>
    void ZoomToActualSize();
}
