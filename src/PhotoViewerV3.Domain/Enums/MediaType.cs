namespace PhotoViewerV3.Domain.Enums;

/// <summary>
/// Medientyp einer Datei. V0.1 nur Image/Video; seit 0.2.8 zusätzlich Archive (ZIP/RAR/7z …, ADR-0022):
/// Archive sind Katalog-Einträge ohne Thumbnail, die über die Inhaltsansicht entpackt werden.
/// Numerische Werte entsprechen der DB-Spalte Files.MediaType (siehe Spike #1).
/// </summary>
public enum MediaType
{
    Image = 0,
    Video = 1,
    Archive = 2,
}
