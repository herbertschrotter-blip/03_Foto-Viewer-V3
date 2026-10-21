using Microsoft.Data.Sqlite;
using PhotoViewerV3.Services.Metadata;

namespace PhotoViewerV3.Gallery.Data;

/// <summary>
/// Aufnahmedatum in der Galerie (ADR-0046): die vier Katalogspalten je Zeile (Kachel-Tooltip, Listenspalte
/// „Aufgenommen“) — gelesen über den Spaltennamen, damit weitere Spalten in <see cref="FileColumns"/> die Reihenfolge
/// nicht verschieben. Sortierung: <see cref="GallerySort.CapturedDesc"/>/<see cref="GallerySort.CapturedAsc"/> über
/// <c>COALESCE(CapturedAt, ModifiedAtUtc)</c> und den Ausdrucks-Index <c>IX_Files_Library_Captured</c>.
/// </summary>
public sealed partial class GalleryPageSource
{
    private const string CaptureDateColumns = "f.CapturedAt, f.CapturedAtSource, f.CapturedAtPrecision, f.CapturedAtOffset";

    private static CaptureDateInfo ReadCaptureDate(SqliteDataReader reader)
    {
        var at = reader.GetOrdinal("CapturedAt");
        var source = reader.GetOrdinal("CapturedAtSource");
        var precision = reader.GetOrdinal("CapturedAtPrecision");
        var offset = reader.GetOrdinal("CapturedAtOffset");
        return CaptureDateInfo.FromColumns(
            reader.GetInt32(0),
            reader.IsDBNull(at) ? null : reader.GetInt64(at),
            reader.IsDBNull(source) ? null : reader.GetInt32(source),
            reader.IsDBNull(precision) ? 0 : reader.GetInt32(precision),
            reader.IsDBNull(offset) ? null : reader.GetInt32(offset));
    }
}
