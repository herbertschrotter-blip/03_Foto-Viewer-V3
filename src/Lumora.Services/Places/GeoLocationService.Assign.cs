using Microsoft.Extensions.Logging;
using Lumora.Domain.Enums;
using Lumora.Services.Metadata;

namespace Lumora.Services.Places;

/// <summary>Neuer Ort je Datei (Vorschlag übernommen, ADR-0062).</summary>
/// <param name="Source">Herkunft im Katalog: <see cref="GeoSource.Track"/>, <see cref="GeoSource.Neighbors"/> bzw. <see cref="GeoSource.Manual"/> (von Hand verschoben).</param>
public sealed record GeoLocationAssignment(int FileId, double Latitude, double Longitude, GeoSource Source);

/// <summary>Ergebnis von <see cref="IGeoAssignService.AssignAsync"/>; <see cref="BatchId"/> 0 = nichts geändert (kein Protokoll).</summary>
/// <param name="AppliedFileIds">Dateien, deren Ort gesetzt wurde (ohne die fehlgeschlagenen).</param>
public sealed record GeoAssignResult(int BatchId, int Written, int CatalogOnly, int Failed, IReadOnlyList<string> Errors, IReadOnlyList<int>? AppliedFileIds = null);

/// <summary>Orte je Datei setzen — Katalog und auf Wunsch die Datei (EXIF bzw. XMP), protokolliert und rückgängig machbar.</summary>
public interface IGeoAssignService
{
    /// <summary>
    /// Setzt je Datei den eigenen Ort in EINEM Protokoll-Batch (<see cref="Domain.Enums.OperationType.SetGeoLocation"/>,
    /// Rückgängig über <see cref="IGeoTagUndoService.UndoAsync"/>). <paramref name="writeToFile"/> = false: nur Katalog,
    /// die Datei wird nicht angefasst.
    /// </summary>
    Task<GeoAssignResult> AssignAsync(IReadOnlyList<GeoLocationAssignment> items, bool writeToFile, bool keepModifiedTime, string description, CancellationToken ct);
}

/// <summary>
/// „Vorschlag übernehmen“ (ADR-0062) über den Weg von „Ort setzen“ (ADR-0050): gleiche Schreibsperre, Scan-Sperre beim
/// Schreiben in Dateien, Temp-Datei + Ersetzen (<see cref="GeoFileWriter"/>), <c>GeoTagUndo</c> je Datei — nur mit eigener
/// Koordinate und Herkunft je Datei und wahlweise nur im Katalog.
/// </summary>
public sealed partial class GeoLocationService : IGeoAssignService
{
    public async Task<GeoAssignResult> AssignAsync(IReadOnlyList<GeoLocationAssignment> items, bool writeToFile, bool keepModifiedTime, string description, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(items);
        var byFile = new Dictionary<int, GeoLocationAssignment>();
        foreach (var item in items)
        {
            if (!new GeoCoordinate(item.Latitude, item.Longitude).IsValid)
            {
                throw new ArgumentOutOfRangeException(nameof(items), "Ungültige Koordinate (Breite −90…90, Länge −180…180, nicht 0/0).");
            }

            byFile[item.FileId] = item;
        }

        if (byFile.Count == 0)
        {
            return new GeoAssignResult(0, 0, 0, 0, []);
        }

        var ids = byFile.Keys.Order().ToArray();
        var libraries = (await Task.Run(() => LoadTargets(ids), ct).ConfigureAwait(false)).Select(r => r.LibraryId).Distinct().ToList();
        var places = byFile.Values.ToDictionary(a => a.FileId, a => _geocoder.Nearest(a.Latitude, a.Longitude)?.PlaceId);
        var errors = new List<string>();
        var changes = new List<Change>();
        var batchId = 0;
        IReadOnlyList<IDisposable> leases = writeToFile ? await AcquireLibrariesAsync(libraries, ct).ConfigureAwait(false) : [];
        try
        {
            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var rows = await Task.Run(() => LoadTargets(ids), ct).ConfigureAwait(false);
                using var scope = writeToFile ? _ownChanges?.Begin() : null;
                await Task.Run(
                    () =>
                    {
                        foreach (var row in rows)
                        {
                            ct.ThrowIfCancellationRequested();
                            var item = byFile[row.Id];
                            var value = new GeoCoordinate(item.Latitude, item.Longitude);
                            if (!writeToFile)
                            {
                                changes.Add(new Change(row, GeoFileWrite.CatalogOnly, value, item.Source, places[row.Id]));
                                continue;
                            }

                            if (row.Missing)
                            {
                                errors.Add($"{Path.GetFileName(row.Path)}: Datei fehlt.");
                                continue;
                            }

                            try
                            {
                                changes.Add(new Change(row, GeoFileWriter.Write(row.Path, row.MediaType, value, keepModifiedTime, scope), value, item.Source, places[row.Id]));
                            }
                            catch (GeoWriteException ex)
                            {
                                errors.Add($"{Path.GetFileName(row.Path)}: {ex.Message}");
                                _logger.LogDebug("Vorschlag übernehmen: Datei {FileId} nicht geschrieben — {Reason}", row.Id, ex.Message);
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                errors.Add($"{Path.GetFileName(row.Path)}: {GeoFileWriter.DescribeIo(ex)}");
                                _logger.LogWarning("Vorschlag übernehmen: Datei {FileId} nicht geschrieben ({Type}, 0x{HResult:X8})", row.Id, ex.GetType().Name, ex.HResult);
                            }
                        }
                    },
                    ct).ConfigureAwait(false);

                if (changes.Count > 0)
                {
                    batchId = await WriteCatalogAsync(changes, description, ct).ConfigureAwait(false);
                }
            }
            finally
            {
                _writeLock.Release();
            }
        }
        catch (OperationCanceledException) when (changes.Count > 0 && batchId == 0)
        {
            RollBackFiles(changes);
            throw;
        }
        finally
        {
            Release(leases);
        }

        var written = changes.Count(c => c.Write.Kind != GeoTagWriteKind.CatalogOnly);
        _logger.LogInformation("Vorschläge übernommen: {Written} Datei(en) geschrieben, {CatalogOnly} nur Katalog, {Failed} fehlgeschlagen", written, changes.Count - written, errors.Count);
        var byLibrary = new Dictionary<int, List<int>>();
        foreach (var change in changes)
        {
            AddTo(byLibrary, change.Row.LibraryId, change.Row.Id);
        }

        Publish(byLibrary);
        return new GeoAssignResult(batchId, written, changes.Count - written, errors.Count, errors, changes.Select(c => c.Row.Id).ToList());
    }
}
