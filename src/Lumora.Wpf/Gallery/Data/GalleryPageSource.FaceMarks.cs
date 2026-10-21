using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Lumora.Gallery.Data;

/// <summary>
/// Gesichter je Datei für Kachel und Listenspalte (0.2.39): bestätigte Personen und Vorschläge wie bisher plus die Zahl
/// der noch unbenannten Gesichter (Zustand 0 oder 1, dazu bestätigt ohne Person). Ignorierte Gesichter zählen nicht.
/// </summary>
public sealed record GalleryFaceMarks(IReadOnlyList<GalleryFacePersonRow> Persons, IReadOnlyDictionary<int, int> UnnamedCounts)
{
    /// <summary>Unbenannte Gesichter der Datei (0, wenn keine).</summary>
    public int UnnamedCount(int fileId) => UnnamedCounts.TryGetValue(fileId, out var count) ? count : 0;
}

/// <summary>
/// „Alle Fotos mit Gesichtern“ (0.2.39) und die Gesichter-Markierung der Kacheln. Der Filter prüft je Datei mit einer
/// abdeckenden Index-Suche in <c>IX_Faces_File_State_Person</c> (FileId, State), ob ein Gesicht mit Zustand 0, 1 oder 2
/// existiert — ignorierte Gesichter (3) zählen nicht, die Tabellenzeile mit dem Fingerabdruck wird nicht gelesen.
/// </summary>
public sealed partial class GalleryPageSource
{
    /// <summary>Mindestens ein erkanntes, nicht ignoriertes Gesicht (unbestätigt, Vorschlag oder bestätigt).</summary>
    private const string AnyFaceClause = "AND EXISTS (SELECT 1 FROM Faces fa WHERE fa.FileId = f.Id AND fa.State IN (0, 1, 2))";

    /// <summary>
    /// Personen, Vorschläge und unbenannte Gesichter für eine Seite — eine Abfrage je 500 Dateien (nicht je Kachel),
    /// siehe <see cref="GetPersonsForFilesAsync"/>.
    /// </summary>
    public Task<GalleryFaceMarks> GetFaceMarksForFilesAsync(IReadOnlyCollection<int> fileIds, CancellationToken ct)
        => RunAsync(conn => ReadFaceMarks(conn, fileIds, ct), ct);
}
