namespace Lumora.Domain.Entities;

/// <summary>
/// Zuweisung eines <see cref="Tag"/> zu einer Datei (Tabelle "FileTags", Schlüssel FileId + TagId).
/// Beide Fremdschlüssel löschen mit (ON DELETE CASCADE): verschwindet die Files-Zeile (Papierkorb,
/// Neu aufbauen) oder das Tag, verschwinden die Zuweisungen ohne weiteren Code.
/// </summary>
public sealed class FileTag
{
    public int FileId { get; set; }
    public int TagId { get; set; }
}
