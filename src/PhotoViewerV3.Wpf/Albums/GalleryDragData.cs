using System.Windows;

namespace PhotoViewerV3.Albums;

/// <summary>
/// Ziehen von Galerie-Dateien auf Ordner (Ordnerbaum) und Alben (ADR-0054). Inhalt: <c>int[]</c> der FileIds.
/// Tasten-Konvention beim Ablegen auf einen Ordner (wie im Explorer): ohne Taste oder mit Alt = <b>Verknüpfen</b>
/// (Standard, nichts wird bewegt), mit Umschalt = <b>Verschieben</b> (mit Rückfrage, protokolliert, rückgängig machbar).
/// Strg (im Explorer „Kopieren“) verknüpft ebenfalls — Lumora kopiert nie in die Bibliothek.
/// </summary>
public static class GalleryDragData
{
    public const string Format = "Lumora.GalleryFileIds";

    public static DataObject Create(int[] fileIds) => new(Format, fileIds);

    public static int[]? Read(IDataObject data)
        => data.GetDataPresent(Format) ? data.GetData(Format) as int[] : null;

    /// <summary>Umschalt gedrückt → Verschieben, sonst Verknüpfen.</summary>
    public static bool IsMove(DragDropKeyStates keys) => (keys & DragDropKeyStates.ShiftKey) != 0;

    /// <summary>Wirkung für den Mauszeiger: Verschieben bzw. Verknüpfen.</summary>
    public static DragDropEffects EffectFor(DragDropKeyStates keys, DragDropEffects allowed)
    {
        var wanted = IsMove(keys) ? DragDropEffects.Move : DragDropEffects.Link;
        return (allowed & wanted) != 0 ? wanted : DragDropEffects.None;
    }
}
