using PhotoViewerV3.Domain.Enums;

namespace PhotoViewerV3.Services.Sorter;

/// <summary>
/// Wurzel einer Bibliothek aus Sicht des Sortierers (ADR-0020): Eingang oder Archiv. <see cref="Path"/> ist
/// normalisiert (<see cref="SorterPaths.NormalizeDirectory"/>).
/// </summary>
public sealed record SorterRoot(int Id, string Path, LibraryRootRole Role, string Name)
{
    public bool IsArchive => Role == LibraryRootRole.Archive;

    public bool IsInbox => Role == LibraryRootRole.Inbox;

    /// <summary>„Archiv“ / „Eingang“.</summary>
    public string RoleText => IsArchive ? "Archiv" : "Eingang";
}

/// <summary>Bibliothek und Wurzel, unter der ein Quellordner liegt, samt allen Wurzeln derselben Bibliothek.</summary>
public sealed record SorterLocation(int LibraryId, SorterRoot Root, IReadOnlyList<SorterRoot> Roots);

/// <summary>
/// Zielprüfung des Sortierers (ADR-0024): Ziele müssen unter einer Wurzel derselben Bibliothek liegen und diese Wurzel
/// muss erreichbar sein. Ziele außerhalb sind gesperrt — die Dateien fielen sonst aus dem Katalog, und ein späteres
/// Rückgängig legte sie mit neuen FileIds an (Bewertung, Favorit und Tags wären weg).
/// </summary>
public static class SorterTargetCheck
{
    /// <summary>
    /// Liefert die Wurzel, unter der <paramref name="normalizedTarget"/> liegt (bei Verschachtelung die längste), und
    /// eine Fehlermeldung, falls das Ziel gesperrt ist; null = Ziel in Ordnung.
    /// </summary>
    public static string? Check(IReadOnlyList<SorterRoot> roots, string normalizedTarget, out SorterRoot? targetRoot)
    {
        ArgumentNullException.ThrowIfNull(roots);
        targetRoot = FindRoot(roots, normalizedTarget);
        if (targetRoot is null)
        {
            return $"Das Ziel „{normalizedTarget}“ liegt außerhalb der Wurzeln dieser Bibliothek. Die Dateien fielen aus dem "
                   + "Katalog (Bewertung, Favorit und Tags gingen verloren) — bitte einen Ordner unter einer Eingangs- oder "
                   + "Archiv-Wurzel wählen.";
        }

        if (!Directory.Exists(targetRoot.Path))
        {
            return $"Die {targetRoot.RoleText}-Wurzel „{targetRoot.Path}“ ist nicht erreichbar (Laufwerk offline?).";
        }

        return null;
    }

    /// <summary>Wurzel, die den Ordner enthält (längster Pfad gewinnt); null, wenn keine.</summary>
    public static SorterRoot? FindRoot(IEnumerable<SorterRoot> roots, string normalizedPath)
        => roots
            .Where(r => SorterPaths.IsInsideRoot(normalizedPath, r.Path))
            .OrderByDescending(r => r.Path.Length)
            .FirstOrDefault();

    /// <summary>true, wenn der Ordner selbst eine Wurzel ist (Wurzeln werden nie gelöscht).</summary>
    public static bool IsRootFolder(IEnumerable<SorterRoot> roots, string normalizedPath)
        => roots.Any(r => SorterPaths.PathEquals(r.Path, normalizedPath));
}
