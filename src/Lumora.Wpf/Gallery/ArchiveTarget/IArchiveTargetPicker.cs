using System.Collections.Generic;
using System.Threading.Tasks;

namespace Lumora.Gallery.ArchiveTarget;

/// <summary>
/// Eingaben für den Dialog „Ins Archiv…“: Archiv-Wurzel der Bibliothek, die zu verschiebenden Dateien, der zuletzt
/// benutzte Zielordner (Setting) und der Vorschlag „Jahr/Monat“ relativ zur Archiv-Wurzel (aus Aufnahme- bzw.
/// Dateidatum der ersten Datei; null, wenn nicht ermittelbar).
/// </summary>
public sealed record ArchiveTargetRequest(
    int LibraryId,
    int ArchiveRootId,
    string ArchiveRootPath,
    string ArchiveRootName,
    IReadOnlyList<int> FileIds,
    string? LastTargetPath,
    string? SuggestedRelativePath);

/// <summary>
/// Zielordner-Auswahl für „Ins Archiv…“ (Implementierung: <see cref="ArchiveTargetDialog"/>; im Rauchtest ein Fake).
/// Liefert den absoluten Zielordner unterhalb der Archiv-Wurzel oder null bei Abbruch.
/// </summary>
public interface IArchiveTargetPicker
{
    Task<string?> PickAsync(ArchiveTargetRequest request);
}
