using System.Drawing;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;

namespace PhotoViewerV3.Services.Faces.Assignment;

/// <summary>
/// Ignorierte Gesichter (ADR-0040): ansehen/zurückholen, ganze Gruppe ignorieren, Mindestgröße. Die Mindestgröße wird
/// vom Hintergrund-Worker angewandt (<see cref="ApplyAutoIgnoreIfPendingAsync"/>, vor Training und Vorschlägen), wenn
/// sich die gespeicherte Schwelle von der zuletzt angewandten unterscheidet — beim Start (einmalig nach dem Update bzw.
/// nach einer Änderung, die vor dem Beenden nicht mehr angewandt wurde) und nach <see cref="SetMinFaceSizeAsync"/>.
/// </summary>
public sealed partial class FaceRecognitionService : IFaceIgnoreService
{
    private bool _autoIgnorePending;

    public async Task<IgnoredFacesPage> GetIgnoredFacesAsync(int offset, int limit, CancellationToken ct = default)
    {
        var libraryId = await GetActiveLibraryIdAsync(ct).ConfigureAwait(false);
        if (libraryId is null)
        {
            return IgnoredFacesPage.Empty;
        }

        return await Task.Run(
            () =>
            {
                using var connection = _factory.OpenSqliteConnection();
                using var count = FaceAssignmentStore.Command(connection, null, """
                    SELECT COUNT(*), COALESCE(SUM(CASE WHEN x.SuggestionScore < 0 THEN 1 ELSE 0 END), 0)
                    FROM Faces x JOIN Files f ON f.Id = x.FileId
                    WHERE f.LibraryId = $lib AND x.State = 3;
                    """, ("$lib", libraryId.Value));
                int total, automatic;
                using (var reader = count.ExecuteReader())
                {
                    reader.Read();
                    total = reader.GetInt32(0);
                    automatic = reader.GetInt32(1);
                }

                using var cmd = FaceAssignmentStore.Command(connection, null, """
                    SELECT x.Id, x.FileId, x.BoxX, x.BoxY, x.BoxW, x.BoxH, x.Size, x.SuggestionScore
                    FROM Faces x JOIN Files f ON f.Id = x.FileId
                    WHERE f.LibraryId = $lib AND x.State = 3
                    ORDER BY x.FileId DESC, x.Id
                    LIMIT $limit OFFSET $offset;
                    """, ("$lib", libraryId.Value), ("$limit", Math.Max(0, limit)), ("$offset", Math.Max(0, offset)));
                var faces = new List<IgnoredFaceInfo>();
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        faces.Add(new IgnoredFaceInfo(
                            reader.GetInt64(0),
                            reader.GetInt32(1),
                            new RectangleF(reader.GetFloat(2), reader.GetFloat(3), reader.GetFloat(4), reader.GetFloat(5)),
                            reader.GetFloat(6),
                            !reader.IsDBNull(7) && reader.GetFloat(7) < 0f));
                    }
                }

                return new IgnoredFacesPage(faces, total, automatic);
            },
            ct).ConfigureAwait(false);
    }

    public async Task<int> UnignoreFacesAsync(IReadOnlyCollection<long> faceIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(faceIds);
        if (faceIds.Count == 0)
        {
            return 0;
        }

        var count = await WriteAsync(silent: false, (connection, tx) =>
        {
            var n = 0;
            foreach (var chunk in faceIds.Distinct().Chunk(500))
            {
                n += FaceAssignmentStore.Execute(connection, tx, $"""
                    UPDATE Faces SET State = 0, PersonId = NULL, SuggestedPersonId = NULL, SuggestionScore = NULL
                    WHERE State = 3 AND Id IN ({FaceAssignmentStore.IdList(chunk)});
                    """);
            }

            return n;
        }, ct).ConfigureAwait(false);
        _logger.LogInformation("{Count} ignorierte(s) Gesicht(er) zurückgeholt", count);
        if (count > 0)
        {
            InvalidateGroups(); // wieder unbekannt → gehören in die Gruppierung
        }

        await NotifyAsync(retrain: count > 0, ct).ConfigureAwait(false); // Vorschläge für die zurückgeholten
        return count;
    }

    public async Task<int> IgnoreGroupAsync(long groupId, CancellationToken ct = default)
    {
        var group = Volatile.Read(ref _groups)?.Groups.FirstOrDefault(g => g.Id == groupId);
        if (group is null)
        {
            return 0;
        }

        var ids = group.Members.Select(m => m.FaceId).ToList();
        var count = await WriteAsync(silent: false, (connection, tx) =>
        {
            var n = 0;
            foreach (var chunk in ids.Chunk(500))
            {
                // nur noch offene: ein inzwischen (z. B. in der Lightbox) bestätigtes Gesicht bleibt bestätigt
                n += FaceAssignmentStore.Execute(connection, tx, $"""
                    UPDATE Faces SET State = 3, PersonId = NULL, SuggestedPersonId = NULL, SuggestionScore = NULL
                    WHERE State IN (0, 1) AND Id IN ({FaceAssignmentStore.IdList(chunk)});
                    """);
            }

            return n;
        }, ct).ConfigureAwait(false);
        _logger.LogInformation("Gruppe mit {Members} Gesichtern ignoriert ({Count} geändert)", ids.Count, count);
        RemoveFromGroups(ids); // alle Mitglieder raus → die Gruppe fällt weg
        await NotifyAsync(retrain: false, ct).ConfigureAwait(false); // offene Gesichter sind keine Lernbeispiele
        return count;
    }

    public Task<int> GetMinFaceSizeAsync(CancellationToken ct = default)
        => Task.Run(
            () =>
            {
                using var connection = _factory.OpenSqliteConnection();
                return FaceAutoIgnore.ReadMinSize(connection, null);
            },
            ct);

    public async Task SetMinFaceSizeAsync(int pixels, CancellationToken ct = default)
    {
        var value = FaceAutoIgnore.Clamp(pixels);
        await WriteAsync(silent: false, (connection, tx) =>
        {
            FaceAutoIgnore.WriteSetting(connection, tx, FaceAutoIgnore.MinSizeKey, value);
            return 0;
        }, ct).ConfigureAwait(false);
        _logger.LogInformation("Mindestgröße für Gesichter: {Pixels} px (0 = aus) — wird im Hintergrund angewandt", value);
        RequestAutoIgnore();
    }

    /// <summary>Mindestgröße beim nächsten Worker-Durchgang prüfen und ggf. auf den Bestand anwenden.</summary>
    private void RequestAutoIgnore()
    {
        lock (_sync)
        {
            _autoIgnorePending = true;
        }

        _wake.Writer.TryWrite(true);
    }

    /// <summary>
    /// Worker: wendet die gespeicherte Schwelle an, wenn sie von der zuletzt angewandten abweicht (eine Transaktion, stille
    /// Warte-Logik wie die Vorschläge). Liefert true, wenn Gesichter zurückgeholt wurden (dann braucht es Vorschläge).
    /// </summary>
    private async Task<bool> ApplyAutoIgnoreIfPendingAsync(CancellationToken ct)
    {
        lock (_sync)
        {
            if (!_autoIgnorePending)
            {
                return false;
            }

            _autoIgnorePending = false;
        }

        try
        {
            var (ignored, restored, minSize) = await WriteAsync(silent: true, (connection, tx) =>
            {
                var min = FaceAutoIgnore.ReadMinSize(connection, tx);
                if (FaceAutoIgnore.ReadApplied(connection, tx) == min)
                {
                    return (new List<long>(), new List<long>(), min);
                }

                var (i, r) = FaceAutoIgnore.Apply(connection, tx, min);
                return (i, r, min);
            }, ct).ConfigureAwait(false);
            if (ignored.Count == 0 && restored.Count == 0)
            {
                return false;
            }

            _logger.LogInformation(
                "Mindestgröße {Pixels} px angewandt: {Ignored} Gesicht(er) automatisch ignoriert, {Restored} zurückgeholt",
                minSize, ignored.Count, restored.Count);
            if (ignored.Count > 0)
            {
                RemoveFromGroups(ignored);
            }

            if (restored.Count > 0)
            {
                InvalidateGroups();
            }

            if (await GetActiveLibraryIdAsync(ct).ConfigureAwait(false) is { } libraryId)
            {
                _messenger.Send(new PersonsChangedMessage(libraryId));
            }

            return restored.Count > 0;
        }
        catch
        {
            lock (_sync)
            {
                _autoIgnorePending = true; // beim nächsten Durchgang erneut versuchen
            }

            throw;
        }
    }
}
