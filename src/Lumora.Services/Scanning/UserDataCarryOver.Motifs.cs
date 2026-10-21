namespace Lumora.Services.Scanning;

/// <summary>
/// Motive über „Neu aufbauen“ (ADR-0052): <c>FileEmbeddings</c> und <c>FileMotifs</c> hängen per ON DELETE CASCADE an
/// <c>Files</c> — ohne Übernahme müsste der Motiv-Index jedes Foto neu rechnen (bei 500 000 Fotos Stunden). Wie die
/// Gesichter (<see cref="ParkFaces"/>) werden die Zeilen in der Transaktion GEPARKT (negative FileId, Fremdschlüssel bis
/// zum Commit aufgeschoben — <c>PRAGMA defer_foreign_keys</c> setzt <see cref="ParkFaces"/>) und nach dem Bulk-Insert
/// über dieselbe Zuordnung alt → neu auf die neuen FileIds gesetzt; was keine neue Datei hat, wird gelöscht.
/// </summary>
public sealed partial class UserDataCarryOver
{
    private const string ParkMotifsSql = """
        PRAGMA defer_foreign_keys = ON;
        UPDATE FileEmbeddings SET FileId = -FileId WHERE FileId IN (SELECT OldFileId FROM temp.pv_carry_files);
        UPDATE FileMotifs SET FileId = -FileId WHERE FileId IN (SELECT OldFileId FROM temp.pv_carry_files);
        """;

    private const string RestoreMotifsSql = """
        UPDATE OR IGNORE FileEmbeddings SET FileId = m.NewFileId FROM temp.pv_carry_map m WHERE FileEmbeddings.FileId = -m.OldFileId;
        UPDATE OR IGNORE FileMotifs SET FileId = m.NewFileId FROM temp.pv_carry_map m WHERE FileMotifs.FileId = -m.OldFileId;
        DELETE FROM FileEmbeddings WHERE FileId < 0;
        DELETE FROM FileMotifs WHERE FileId < 0;
        """;

    private void ParkMotifs() => Execute(_connection, _transaction, ParkMotifsSql, _libraryId);

    private void RestoreMotifs() => Execute(_connection, _transaction, RestoreMotifsSql, _libraryId);
}
