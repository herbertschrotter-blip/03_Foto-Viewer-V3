using System.Globalization;
using Microsoft.Data.Sqlite;

namespace PhotoViewerV3.Services.Duplicates;

/// <summary>
/// Gemeinsame SQL-Bausteine der Doppelten-Erkennung (ADR-0053) — auch für den Galerie-Filter „nur Doppelte“ im Wpf-Projekt.
/// Ein Fingerabdruck gilt nur für die Dateiversion, aus der er stammt (Größe und Änderungszeit wie in der Files-Zeile).
/// </summary>
public static class DuplicateSql
{
    /// <summary>Gültige Inhalts-Prüfsumme der Zeile <paramref name="h"/> (FileFingerprints) zur Datei <paramref name="f"/> (Files).</summary>
    public static string ValidContent(string h, string f)
        => $"{h}.ContentHash IS NOT NULL AND {h}.ContentSize = {f}.Size AND {h}.ContentModifiedAt = {f}.ModifiedAtUtc";

    /// <summary>Gültiger, aussagekräftiger Bild-Fingerabdruck (aktuelle Version, nicht „fast einfarbig“).</summary>
    public static string ValidImage(string h, string f)
        => $"{h}.ImageHash IS NOT NULL AND {h}.ImageHashVersion = {PerceptualHash.Version.ToString(CultureInfo.InvariantCulture)} "
           + $"AND {h}.ImageModifiedAt = {f}.ModifiedAtUtc AND {h}.ImageLowDetail = 0";

    /// <summary>Bibliotheks-Ids als Literal-Liste für <c>IN (…)</c> (nur Ganzzahlen aus dem Katalog; leer → -1).</summary>
    public static string IdList(IEnumerable<int> ids)
    {
        var text = string.Join(",", ids.Distinct().Select(id => id.ToString(CultureInfo.InvariantCulture)));
        return text.Length == 0 ? "-1" : text;
    }

    /// <summary>
    /// Bedingung „die Datei <paramref name="f"/> hat ein byte-gleiches Gegenstück in einer der Bibliotheken
    /// <paramref name="libraryIds"/>“ — für den Galerie-Filter „nur Doppelte“. Nicht korreliert: die Prüfsummen mit mehr als
    /// einer gültigen Datei im Bereich werden einmal über <c>IX_FileFingerprints_ContentHash</c> gesammelt (SQLite
    /// materialisiert die Liste), die Galerie prüft je Zeile nur die Zugehörigkeit (Spike 15: ein korreliertes EXISTS kostete
    /// bei 500 000 Dateien ≈ 5 s).
    /// </summary>
    public static string HasExactDuplicate(string f, IEnumerable<int> libraryIds)
    {
        var ids = IdList(libraryIds);
        return $"""
            {f}.Id IN (SELECT dh.FileId FROM FileFingerprints dh CROSS JOIN Files df ON df.Id = dh.FileId
                       WHERE df.LibraryId IN ({ids}) AND df.Missing = 0 AND {ValidContent("dh", "df")}
                         AND dh.ContentHash IN (SELECT dh2.ContentHash FROM FileFingerprints dh2 CROSS JOIN Files df2 ON df2.Id = dh2.FileId
                                                WHERE df2.LibraryId IN ({ids}) AND df2.Missing = 0 AND {ValidContent("dh2", "df2")}
                                                GROUP BY dh2.ContentHash HAVING COUNT(*) > 1))
            """;
    }

    /// <summary>
    /// Zahl der Dateien unter einer Eingangs-Wurzel der Bibliotheken <paramref name="libraryIds"/>, die byte-gleich schon unter
    /// der Bibliotheks-Wurzel (Archiv, Rolle 1) einer dieser Bibliotheken liegen („N Dateien im Eingang sind schon in der Bibliothek“).
    /// Erst die Prüfsummen der Bibliotheks-Dateien (einmal gesammelt), dann die Eingangs-Dateien mit diesen Prüfsummen über den Index.
    /// </summary>
    public static int CountInboxAlreadyInLibrary(SqliteConnection connection, IReadOnlyCollection<int> libraryIds)
    {
        using var cmd = connection.CreateCommand();
        var ids = IdList(libraryIds);
        cmd.CommandText = $"""
            SELECT COUNT(*) FROM FileFingerprints h
            CROSS JOIN Files f ON f.Id = h.FileId
            CROSS JOIN Folders d ON d.Id = f.FolderId
            CROSS JOIN LibraryRoots r ON r.Id = d.RootId
            WHERE h.ContentHash IN (SELECT h2.ContentHash FROM FileFingerprints h2
                                    CROSS JOIN Files f2 ON f2.Id = h2.FileId
                                    CROSS JOIN Folders d2 ON d2.Id = f2.FolderId
                                    CROSS JOIN LibraryRoots r2 ON r2.Id = d2.RootId
                                    WHERE r2.Role = 1 AND f2.LibraryId IN ({ids}) AND f2.Missing = 0 AND {ValidContent("h2", "f2")})
              AND r.Role = 0 AND f.LibraryId IN ({ids}) AND f.Missing = 0 AND {ValidContent("h", "f")};
            """;
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
}