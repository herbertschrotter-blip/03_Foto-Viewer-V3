namespace PhotoViewerV3.Services.Scanning;

/// <summary>
/// Gesichter über „Neu aufbauen“ (ADR-0035): <c>Faces</c> und <c>FaceScans</c> hängen per ON DELETE CASCADE an
/// <c>Files</c> und gingen mit den alten Zeilen verloren — samt Fingerabdrücken (Stunden Rechenzeit bei großen
/// Bibliotheken) und bestätigten Zuordnungen. Statt die Zeilen (2 KB Fingerabdruck je Gesicht) in TEMP-Tabellen zu
/// kopieren, werden sie in der Transaktion GEPARKT: <c>FileId</c> wird negativ (zeigt auf keine Datei, die Kaskade beim
/// Löschen der alten Files-Zeilen erreicht sie nicht), Fremdschlüssel bis zum Commit aufgeschoben
/// (<c>PRAGMA defer_foreign_keys</c>, gilt nur für diese Transaktion). Nach dem Bulk-Insert bekommen sie über dieselbe
/// Zuordnung alt → neu wie Bewertungen und Tags (<c>pv_carry_map</c>, Ordnerpfad + Name) die neuen FileIds; was keine
/// neue Datei hat, wird gelöscht. Face-Ids bleiben gleich — <c>FaceRejections</c> und <c>Persons.CoverFaceId</c> gelten
/// weiter. Vor dem Commit steht keine negative FileId mehr in der DB (sonst scheitert der Commit an den Fremdschlüsseln
/// und der alte Katalog bleibt unverändert).
/// </summary>
public sealed partial class UserDataCarryOver
{
    private const string ParkFacesSql = """
        PRAGMA defer_foreign_keys = ON;
        UPDATE Faces SET FileId = -FileId WHERE FileId IN (SELECT OldFileId FROM temp.pv_carry_files);
        UPDATE FaceScans SET FileId = -FileId WHERE FileId IN (SELECT OldFileId FROM temp.pv_carry_files);
        """;

    private const string RestoreFacesSql = """
        UPDATE Faces SET FileId = m.NewFileId
        FROM temp.pv_carry_map m
        WHERE Faces.FileId = -m.OldFileId;
        """;

    // OR IGNORE: zwei alte Dateien, die auf dieselbe neue fallen (nur bei case-sensitiven Ordnern), behalten einen Scan.
    private const string RestoreFaceScansSql = """
        UPDATE OR IGNORE FaceScans SET FileId = m.NewFileId
        FROM temp.pv_carry_map m
        WHERE FaceScans.FileId = -m.OldFileId;
        """;

    private const string DropParkedFacesSql = """
        UPDATE Persons SET CoverFaceId = NULL WHERE CoverFaceId IN (SELECT Id FROM Faces WHERE FileId < 0);
        DELETE FROM Faces WHERE FileId < 0;
        DELETE FROM FaceScans WHERE FileId < 0;
        """;

    /// <summary>Anzahl geparkter Gesichter (Teil der Sicherung).</summary>
    public int CapturedFaces { get; private set; }

    private void ParkFaces()
    {
        Execute(_connection, _transaction, ParkFacesSql, _libraryId);
        CapturedFaces = CountParkedFaces();
    }

    /// <summary>Setzt geparkte Gesichter und Scans auf die neuen FileIds; liefert die Zahl übernommener Gesichter.</summary>
    private int RestoreFaces()
    {
        var faces = Execute(_connection, _transaction, RestoreFacesSql, _libraryId);
        Execute(_connection, _transaction, RestoreFaceScansSql, _libraryId);
        Execute(_connection, _transaction, DropParkedFacesSql, _libraryId);
        return faces;
    }

    private int CountParkedFaces()
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        cmd.CommandText = "SELECT COUNT(*) FROM Faces WHERE FileId < 0;";
        return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
