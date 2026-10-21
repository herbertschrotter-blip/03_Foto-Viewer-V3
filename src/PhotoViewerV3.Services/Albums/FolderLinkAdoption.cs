using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PhotoViewerV3.Data;
using PhotoViewerV3.Services.Operations;

namespace PhotoViewerV3.Services.Albums;

/// <summary>
/// „Ordner in die Bibliothek übernehmen“ (ADR-0034) erhält Ordner-Verknüpfungen (ADR-0054): die Dateien behalten ihre
/// FileId (ihre eigenen Verknüpfungen bleiben ohnehin), aber die Quellordner verschwinden — Fotos, die IN einen
/// übernommenen Ordner verknüpft waren, wandern mit in den zugehörigen Zielordner (gleiche Zuordnung wie die Dateien,
/// zusammengezogene Zwischenordner eingeschlossen). Läuft nach dem Verschieben der Dateien, vor dem Papierkorb der Quelle.
/// </summary>
internal static class FolderLinkAdoption
{
    private const string MoveSql = """
        INSERT OR IGNORE INTO FolderLinks (FolderId, FileId, AddedAtUtc)
        SELECT t.Id, l.FileId, l.AddedAtUtc FROM FolderLinks l
        JOIN Folders s ON s.Id = l.FolderId
        JOIN Folders t ON t.LibraryId = s.LibraryId AND lower(t.Path) = lower($target)
        JOIN Files f ON f.Id = l.FileId
        WHERE s.LibraryId = $lib AND lower(s.Path) = lower($source) AND f.FolderId <> t.Id;
        DELETE FROM FolderLinks
        WHERE FolderId IN (SELECT s.Id FROM Folders s WHERE s.LibraryId = $lib AND lower(s.Path) = lower($source))
          AND EXISTS (SELECT 1 FROM Folders t WHERE t.LibraryId = $lib AND lower(t.Path) = lower($target));
        """;

    public static async Task MoveLinksAsync(LibraryContext ctx, int libraryId, FolderAdoptionPlan plan, CancellationToken ct)
    {
        if (!await ctx.FolderLinks.AnyAsync(ct).ConfigureAwait(false))
        {
            return; // keine Verknüpfungen im Katalog — nichts zu tun
        }

        foreach (var folder in plan.Folders)
        {
            foreach (var source in folder.SourcePaths.Concat(folder.CollapsedPaths))
            {
                await ctx.Database.ExecuteSqlRawAsync(
                    MoveSql,
                    [new SqliteParameter("$lib", libraryId), new SqliteParameter("$source", Trim(source)), new SqliteParameter("$target", Trim(folder.TargetPath))],
                    ct).ConfigureAwait(false);
            }
        }
    }

    private static string Trim(string path) => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
