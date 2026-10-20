namespace PhotoViewerV3.Domain.Entities;

/// <summary>
/// Wert einer Datei in einer Kategorie (Tabelle "FileCategoryValues", Schlüssel FileId + CategoryId → ein Wert je
/// Kategorie und Datei). Alle drei Fremdschlüssel löschen mit (ON DELETE CASCADE): Files-Zeile weg (Papierkorb,
/// Neu aufbauen), Wert oder Kategorie gelöscht → Zuweisung weg. <see cref="ValueId"/> gehört immer zu
/// <see cref="CategoryId"/> (prüft der CategoryService beim Schreiben).
/// </summary>
public sealed class FileCategoryValue
{
    public int FileId { get; set; }
    public int CategoryId { get; set; }
    public int ValueId { get; set; }
}
