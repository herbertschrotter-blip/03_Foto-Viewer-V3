namespace PhotoViewerV3.Domain.Enums;

/// <summary>
/// Medientyp einer Datei. V0.1 nur Image/Video.
/// Numerische Werte entsprechen der DB-Spalte Files.MediaType (siehe Spike #1).
/// </summary>
public enum MediaType
{
    Image = 0,
    Video = 1,
}
