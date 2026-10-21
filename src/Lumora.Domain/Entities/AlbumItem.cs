namespace Lumora.Domain.Entities;

/// <summary>
/// Foto in einem eigenen Album (ADR-0054, Tabelle "AlbumItems", Schlüssel AlbumId + FileId). Beide Fremdschlüssel löschen
/// mit (ON DELETE CASCADE): Album gelöscht → Verknüpfungen weg, das Foto bleibt; Files-Zeile gelöscht → Verknüpfung weg
/// (Datei-Operationen protokollieren sie vorher in <see cref="FileLinkUndo"/>).
/// </summary>
public sealed class AlbumItem
{
    public int AlbumId { get; set; }

    public int FileId { get; set; }

    public DateTime AddedAtUtc { get; set; }
}
