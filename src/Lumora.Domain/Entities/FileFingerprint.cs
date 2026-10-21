namespace Lumora.Domain.Entities;

/// <summary>
/// Fingerabdrücke einer Datei für „Doppelte erkennen“ (ADR-0053, Tabelle "FileFingerprints", Schlüssel FileId).
/// <para>
/// Inhalts-Prüfsumme: SHA-256 über alle Bytes (<see cref="ContentHash"/>, 32 Bytes) — nur für Dateien, deren Größe im
/// Katalog mindestens zweimal vorkommt (Vorfilter). Gültig nur für die gelesene Dateiversion: <see cref="ContentSize"/> und
/// <see cref="ContentModifiedAt"/> (Unix-Sekunden wie <c>Files.ModifiedAtUtc</c>) müssen zur Files-Zeile passen, sonst ist
/// sie veraltet und wird neu berechnet.
/// </para>
/// <para>
/// Bild-Fingerabdruck: 64-Bit-pHash (DCT) aus dem Vorschaubild (<see cref="ImageHash"/>, als vorzeichenbehaftete Zahl
/// gespeichert) in Version <see cref="ImageHashVersion"/> für die Dateiversion <see cref="ImageModifiedAt"/>;
/// <see cref="ImageLowDetail"/> = fast einfarbiges Bild (zählt nicht als „ähnlich“), <see cref="ImageError"/> ≠ null =
/// nicht dekodierbar (mit derselben Version kein neuer Versuch). Files-Zeile weg → Zeile weg (ON DELETE CASCADE).
/// </para>
/// </summary>
public sealed class FileFingerprint
{
    public int FileId { get; set; }

    public byte[]? ContentHash { get; set; }

    public long? ContentSize { get; set; }

    public long? ContentModifiedAt { get; set; }

    public long? ImageHash { get; set; }

    public int? ImageHashVersion { get; set; }

    public long? ImageModifiedAt { get; set; }

    public bool ImageLowDetail { get; set; }

    public string? ImageError { get; set; }
}
