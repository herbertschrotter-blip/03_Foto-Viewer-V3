using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using PhotoViewerV3.Services.Database;
using PhotoViewerV3.Services.Faces.Assignment;
using PhotoViewerV3.Services.Faces.Crops;
using PhotoViewerV3.Services.Faces.Indexing;
using PhotoViewerV3.Services.Infrastructure;

namespace PhotoViewerV3.Services.Faces.Erase;

/// <summary>Ergebnis von „Gesichtsdaten löschen“ (nur Zahlen, nie Namen oder Pfade im Log).</summary>
public sealed record FaceDataEraseResult(int Faces, int Scans, int Rejections, int Persons, int CacheFiles);

/// <summary>
/// „Gesichtsdaten löschen…“ (Einstellungen → Gesichtserkennung, 0.2.38): schaltet die Gesichtserkennung aus und löscht
/// für EINE Bibliothek alle Gesichter samt Fingerabdrücken, Analyse-Vermerke (FaceScans), Ablehnungen und den
/// Gesichter-Cache; Personen werden gelöscht, sobald sie danach in keiner Bibliothek mehr ein Gesicht haben (Personen
/// sind global wie Tags). Fotos, Bewertungen, Tags und Kategorien bleiben unberührt.
/// </summary>
public interface IFaceDataEraser
{
    Task<FaceDataEraseResult> EraseLibraryAsync(int libraryId, CancellationToken ct = default);
}

/// <summary>Ort des Gesichter-Caches (Ausschnitte für Personen-Kacheln) einer Bibliothek: <c>thumbs\{libraryId}\faces\</c>.</summary>
public static class FaceDataCache
{
    public const string FolderName = "faces";

    public static string GetFolder(IAppDataPaths paths, int libraryId)
        => Path.Combine(paths.GetLibraryThumbnailsPath(libraryId), FolderName);
}

public sealed class FaceDataEraser : IFaceDataEraser
{
    private readonly ILibraryContextFactory _factory;
    private readonly IAppDataPaths _paths;
    private readonly IFaceIndexService _index;
    private readonly ILogger<FaceDataEraser> _logger;
    private readonly IMessenger _messenger;
    private readonly TimeSpan _settleDelay;
    private readonly IFaceRecognitionService? _recognition;
    private readonly IFaceCropService? _crops;

    /// <param name="settleDelay">
    /// Wartezeit vor der Nachkontrolle (Standard 1,5 s): so lange kann ein Schreib-Stapel des Hintergrund-Index noch
    /// committen, der kurz vor dem Ausschalten begonnen hat. Tests setzen 0.
    /// </param>
    /// <param name="recognition">Zuordnungsdienst (P1): verwirft danach seine zwischengespeicherten Modelle/Vorschläge.</param>
    /// <param name="crops">Gesichts-Ausschnitte (P1): danach alle löschen.</param>
    public FaceDataEraser(
        ILibraryContextFactory factory,
        IAppDataPaths paths,
        IFaceIndexService index,
        ILogger<FaceDataEraser> logger,
        IMessenger? messenger = null,
        TimeSpan? settleDelay = null,
        IFaceRecognitionService? recognition = null,
        IFaceCropService? crops = null)
    {
        _recognition = recognition;
        _crops = crops;
        _factory = factory;
        _paths = paths;
        _index = index;
        _logger = logger;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
        _settleDelay = settleDelay ?? TimeSpan.FromSeconds(1.5);
    }

    public async Task<FaceDataEraseResult> EraseLibraryAsync(int libraryId, CancellationToken ct = default)
    {
        // Erst aus: der Hintergrund-Index darf nichts nachschreiben (Abbruch verwirft ungeschriebene Ergebnisse; ein
        // schon laufender Schreib-Stapel kommt wegen der Schreibsperre vor unserer Transaktion dran).
        await _index.SetEnabledAsync(false, ct).ConfigureAwait(false);
        _index.CancelRun();

        var (fileIds, faces, scans, rejections, persons) = await Task.Run(() => DeleteAsync(libraryId, ct), ct).ConfigureAwait(false);

        // Sicherheitsnetz gegen einen Stapel des Index, der vor dem Ausschalten begann und erst nach uns committet:
        // kurz warten, dann Gesichter UND Analyse-Vermerke nachzählen (ein Vermerk ohne Gesicht ließe das Foto beim
        // Wiedereinschalten aus).
        if (_settleDelay > TimeSpan.Zero)
        {
            await Task.Delay(_settleDelay, ct).ConfigureAwait(false);
        }

        var residual = await Task.Run(() => CountResidual(libraryId), ct).ConfigureAwait(false);
        if (residual > 0)
        {
            var second = await Task.Run(() => DeleteAsync(libraryId, ct), ct).ConfigureAwait(false);
            faces += second.Faces;
            scans += second.Scans;
            rejections += second.Rejections;
            persons += second.Persons;
            fileIds = fileIds.Concat(second.FileIds).Distinct().ToList();
        }

        var cacheFiles = DeleteCache(libraryId);
        InvalidateServices();
        var result = new FaceDataEraseResult(faces, scans, rejections, persons, cacheFiles);
        _logger.LogInformation(
            "Gesichtsdaten der Bibliothek {LibraryId} gelöscht: {Faces} Gesichter, {Scans} Analyse-Vermerke, {Rejections} Ablehnungen, {Persons} Personen, {Cache} Cache-Dateien",
            libraryId, faces, scans, rejections, persons, cacheFiles);

        Publish(new FacesChangedMessage(libraryId, fileIds));
        Publish(new PersonsChangedMessage(libraryId));
        return result;
    }

    private async Task<(List<int> FileIds, int Faces, int Scans, int Rejections, int Persons)> DeleteAsync(int libraryId, CancellationToken ct)
    {
        using var connection = _factory.OpenSqliteConnection();
        using var tx = await _factory.BusyRetry.BeginTransactionAsync(connection, ct).ConfigureAwait(false);

        var fileIds = new List<int>();
        using (var cmd = Command(connection, tx, libraryId,
                   "SELECT DISTINCT fa.FileId FROM Faces fa WHERE fa.FileId IN (SELECT f.Id FROM Files f WHERE f.LibraryId = $lib);"))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                fileIds.Add(reader.GetInt32(0));
            }
        }

        // Personen, die in dieser Bibliothek ein Gesicht oder einen Vorschlag haben — nur sie kommen fürs Löschen infrage
        // (eine Person ohne Gesichter, z. B. gerade angelegt oder nur in einer anderen Bibliothek, bleibt).
        var candidates = new List<int>();
        using (var cmd = Command(connection, tx, libraryId, """
                   SELECT fa.PersonId FROM Faces fa
                   WHERE fa.FileId IN (SELECT f.Id FROM Files f WHERE f.LibraryId = $lib) AND fa.PersonId IS NOT NULL
                   UNION
                   SELECT fa.SuggestedPersonId FROM Faces fa
                   WHERE fa.FileId IN (SELECT f.Id FROM Files f WHERE f.LibraryId = $lib) AND fa.SuggestedPersonId IS NOT NULL;
                   """))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                candidates.Add(reader.GetInt32(0));
            }
        }

        var rejections = Execute(connection, tx, libraryId, """
            DELETE FROM FaceRejections WHERE FaceId IN (
                SELECT fa.Id FROM Faces fa WHERE fa.FileId IN (SELECT f.Id FROM Files f WHERE f.LibraryId = $lib));
            """);
        var faces = Execute(connection, tx, libraryId,
            "DELETE FROM Faces WHERE FileId IN (SELECT f.Id FROM Files f WHERE f.LibraryId = $lib);");
        var scans = Execute(connection, tx, libraryId,
            "DELETE FROM FaceScans WHERE FileId IN (SELECT f.Id FROM Files f WHERE f.LibraryId = $lib);");

        // Personen sind global: von den Kandidaten nur die löschen, die danach in keiner Bibliothek mehr ein Gesicht
        // (oder einen Vorschlag) haben.
        var persons = 0;
        foreach (var chunk in candidates.Chunk(500))
        {
            using var cmd = Command(connection, tx, libraryId, string.Empty);
            var names = new List<string>(chunk.Length);
            for (var i = 0; i < chunk.Length; i++)
            {
                names.Add("$p" + i);
                cmd.Parameters.AddWithValue("$p" + i, chunk[i]);
            }

            cmd.CommandText = $"DELETE FROM Persons WHERE Id IN ({string.Join(",", names)}) " +
                              "AND NOT EXISTS (SELECT 1 FROM Faces fa WHERE fa.PersonId = Persons.Id) " +
                              "AND NOT EXISTS (SELECT 1 FROM Faces fs WHERE fs.SuggestedPersonId = Persons.Id);";
            persons += cmd.ExecuteNonQuery();
        }
        Execute(connection, tx, libraryId, """
            UPDATE Persons SET CoverFaceId = NULL
            WHERE CoverFaceId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM Faces fa WHERE fa.Id = Persons.CoverFaceId);
            """);

        tx.Commit();
        return (fileIds, faces, scans, rejections, persons);
    }

    /// <summary>
    /// Zuordnungsdienst und Ausschnitt-Dienst (P1) nachziehen — bewusst ohne <c>await</c> in einer nicht-asynchronen
    /// Methode, damit der Aufruf unabhängig vom Rückgabetyp kompiliert. Fehler dort verhindern das Löschen nicht.
    /// </summary>
    private void InvalidateServices()
    {
        try
        {
            _recognition?.InvalidateAll();
            _crops?.DeleteAll();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Gesichtsdaten gelöscht, aber Zuordnungs-/Ausschnitt-Dienst konnte nicht zurückgesetzt werden");
        }
    }

    private long CountResidual(int libraryId)
    {
        using var connection = _factory.OpenSqliteConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT (SELECT COUNT(*) FROM Faces fa WHERE fa.FileId IN (SELECT f.Id FROM Files f WHERE f.LibraryId = $lib)) " +
                          "+ (SELECT COUNT(*) FROM FaceScans s WHERE s.FileId IN (SELECT f.Id FROM Files f WHERE f.LibraryId = $lib));";
        cmd.Parameters.AddWithValue("$lib", libraryId);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private int DeleteCache(int libraryId)
    {
        var folder = FaceDataCache.GetFolder(_paths, libraryId);
        if (!Directory.Exists(folder))
        {
            return 0;
        }

        var count = 0;
        try
        {
            count = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Count();
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Gesichter-Cache der Bibliothek {LibraryId} konnte nicht vollständig gelöscht werden", libraryId);
        }

        return count;
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction tx, int libraryId, string sql)
    {
        var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$lib", libraryId);
        return cmd;
    }

    private static int Execute(SqliteConnection connection, SqliteTransaction tx, int libraryId, string sql)
    {
        using var cmd = Command(connection, tx, libraryId, sql);
        return cmd.ExecuteNonQuery();
    }

    private void Publish<TMessage>(TMessage message)
        where TMessage : class
    {
        try
        {
            _messenger.Send(message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Empfänger der {Message} warf eine Ausnahme", typeof(TMessage).Name);
        }
    }
}
