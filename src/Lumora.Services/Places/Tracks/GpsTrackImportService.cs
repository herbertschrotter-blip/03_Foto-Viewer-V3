using System.IO.Compression;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Lumora.Services.Infrastructure;
using Lumora.Services.Library;
using Lumora.Services.Messaging;

namespace Lumora.Services.Places.Tracks;

/// <summary>Zustand des Einlesens (Statusleiste, Einstellungen › Orte).</summary>
/// <param name="FilesRead">Gelesene Dateien (FIT/GPX/TCX, auch aus ZIPs).</param>
/// <param name="FilesFound">Bisher gefundene Dateien (wächst beim Öffnen verschachtelter ZIPs).</param>
public sealed record GpsTrackImportStatus(bool IsRunning, int FilesRead, int FilesFound, int Imported, int Duplicates)
{
    public static GpsTrackImportStatus Idle { get; } = new(false, 0, 0, 0, 0);
}

/// <summary>Ergebnis eines Einlesens.</summary>
/// <param name="WithoutGps">Aktivitäten ohne Position (Laufband, Schwimmbad, Krafttraining).</param>
/// <param name="Failed">Nicht lesbare Dateien.</param>
public sealed record GpsTrackImportReport(int FilesRead, int Imported, int Duplicates, int WithoutGps, int Failed, bool Cancelled, string? Error = null)
{
    /// <summary>„37 Aktivitäten eingelesen, 12 schon vorhanden, 5 ohne GPS“.</summary>
    public string Text
    {
        get
        {
            if (Error is not null)
            {
                return Error;
            }

            var parts = new List<string> { $"{Imported} {(Imported == 1 ? "Aktivität" : "Aktivitäten")} eingelesen" };
            if (Duplicates > 0)
            {
                parts.Add($"{Duplicates} schon vorhanden");
            }

            if (WithoutGps > 0)
            {
                parts.Add($"{WithoutGps} ohne GPS");
            }

            if (Failed > 0)
            {
                parts.Add($"{Failed} nicht lesbar");
            }

            return string.Join(", ", parts) + (Cancelled ? " — abgebrochen" : string.Empty);
        }
    }
}

/// <summary>GPS-Spuren aus Dateien einlesen (ADR-0062): Garmin-Datenexport-ZIP (auch verschachtelt), FIT/GPX/TCX, Ordner.</summary>
public interface IGpsTrackImportService
{
    GpsTrackImportStatus Status { get; }

    bool IsRunning { get; }

    /// <summary>Liest Dateien bzw. Ordner in die Gruppe des aktuellen Bibliotheksbereichs ein; Doppelte werden übersprungen.</summary>
    Task<GpsTrackImportReport> ImportAsync(IReadOnlyList<string> paths, CancellationToken ct = default);

    /// <summary>Bricht ein laufendes Einlesen ab (bereits gespeicherte Aktivitäten bleiben).</summary>
    void Cancel();
}

/// <summary>
/// Einlesen der GPS-Spuren (ADR-0062). ZIP-Einträge werden gestreamt; FIT/GPX/TCX-Einträge landen höchstens
/// <see cref="FitTrackReader.MaxFileBytes"/> groß im Speicher, verschachtelte ZIPs (Garmin: <c>DI_CONNECT/…/UploadedFiles_*.zip</c>)
/// werden einzeln in den Temp-Ordner des Katalogs (<see cref="ICatalogStoragePaths.TempRoot"/>, in einer versteckten
/// Bibliothek im Container) kopiert, gelesen und sofort gelöscht — nichts wird unkontrolliert entpackt, höchstens zwei
/// Ebenen tief. Doppel-Erkennung über die Prüfsumme der Punkte (vorhandene der Gruppe + im selben Lauf). Speichern in
/// Stapeln zu <see cref="InsertBatchSize"/> (eine Transaktion je Stapel). Es wird nichts gesendet; Protokolle nennen nur
/// Zahlen, nie Dateinamen (Garmin-Exportnamen enthalten die E-Mail-Adresse).
/// </summary>
public sealed class GpsTrackImportService : IGpsTrackImportService
{
    public const int InsertBatchSize = 50;
    public const int MaxZipDepth = 2;

    /// <summary>Größte verschachtelte ZIP-Datei, die in den Temp-Ordner kopiert wird.</summary>
    public const long MaxNestedZipBytes = 8L * 1024 * 1024 * 1024;

    private static readonly string[] TrackExtensions = [".fit", ".gpx", ".tcx"];

    private readonly IGpsTrackStore _store;
    private readonly ILibraryScope _scope;
    private readonly ICatalogStoragePaths _storage;
    private readonly ILogger<GpsTrackImportService> _logger;
    private readonly IMessenger _messenger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _running;
    private GpsTrackImportStatus _status = GpsTrackImportStatus.Idle;
    private DateTime _lastStatusUtc;

    public GpsTrackImportService(IGpsTrackStore store, ILibraryScope scope, ICatalogStoragePaths storage, ILogger<GpsTrackImportService> logger, IMessenger? messenger = null)
    {
        _store = store;
        _scope = scope;
        _storage = storage;
        _logger = logger;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
    }

    public GpsTrackImportStatus Status => Volatile.Read(ref _status);

    public bool IsRunning => Status.IsRunning;

    public void Cancel()
    {
        try
        {
            _running?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // schon fertig
        }
    }

    /// <summary>Endung, die das Einlesen versteht (.zip, .fit, .gpx, .tcx).</summary>
    public static bool IsSupported(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".zip", StringComparison.OrdinalIgnoreCase) || TrackExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<GpsTrackImportReport> ImportAsync(IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var scope = await _scope.GetCurrentAsync(ct).ConfigureAwait(false);
        if (!scope.HasLibrary)
        {
            return new GpsTrackImportReport(0, 0, 0, 0, 0, false, "Keine Bibliothek — Spuren gehören zu einer Bibliothek bzw. Gruppe.");
        }

        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            return new GpsTrackImportReport(0, 0, 0, 0, 0, false, "Es werden gerade schon Spuren eingelesen.");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _running = cts;
        Run? run = null;
        try
        {
            run = new Run(this, scope.GroupId, await _store.LoadChecksumsAsync(scope.GroupId, cts.Token).ConfigureAwait(false), cts.Token);
            PublishStatus(run.Status(true), force: true);
            await Task.Run(() => run.ProcessAsync(paths), cts.Token).ConfigureAwait(false);
            await run.FlushAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && run is not null)
        {
            run.Cancelled = true;
            try
            {
                await run.FlushAsync(CancellationToken.None).ConfigureAwait(false); // schon gelesene behalten
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "GPS-Spuren: gelesene Aktivitäten nach dem Abbruch nicht gespeichert");
            }
        }
        finally
        {
            _running = null;
            PublishStatus(GpsTrackImportStatus.Idle, force: true);
            _gate.Release();
            run?.DeleteTemp();
        }

        ArgumentNullException.ThrowIfNull(run); // ohne Lauf wäre die Ausnahme oben schon weitergereicht
        var report = new GpsTrackImportReport(run.FilesRead, run.Imported, run.Duplicates, run.WithoutGps, run.Failed, run.Cancelled);
        _logger.LogInformation(
            "GPS-Spuren eingelesen: {Files} Dateien, {Imported} neu, {Duplicates} doppelt, {WithoutGps} ohne GPS, {Failed} nicht lesbar{Cancelled}",
            report.FilesRead, report.Imported, report.Duplicates, report.WithoutGps, report.Failed, report.Cancelled ? " (abgebrochen)" : string.Empty);
        if (run.Imported > 0)
        {
            try
            {
                _messenger.Send(new GpsTracksChangedMessage(scope.GroupId));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Empfänger der GpsTracksChangedMessage warf eine Ausnahme");
            }
        }

        return report;
    }

    private void PublishStatus(GpsTrackImportStatus status, bool force = false)
    {
        Volatile.Write(ref _status, status);
        var now = DateTime.UtcNow;
        if (!force && now - _lastStatusUtc < TimeSpan.FromMilliseconds(400))
        {
            return;
        }

        _lastStatusUtc = now;
        try
        {
            _messenger.Send(new GpsTrackImportStatusMessage(status));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Empfänger der GpsTrackImportStatusMessage warf eine Ausnahme");
        }
    }

    /// <summary>Ein Durchgang: Zähler, Puffer, Temp-Ordner.</summary>
    private sealed class Run(GpsTrackImportService owner, int groupId, HashSet<string> known, CancellationToken ct)
    {
        private readonly List<NewGpsTrack> _buffer = new();
        private string? _tempDir;

        public int FilesFound;
        public int FilesRead;
        public int Imported;
        public int Duplicates;
        public int WithoutGps;
        public int Failed;
        public bool Cancelled;

        public GpsTrackImportStatus Status(bool running) => new(running, FilesRead, Math.Max(FilesFound, FilesRead), Imported, Duplicates);

        public async Task ProcessAsync(IReadOnlyList<string> paths)
        {
            foreach (var path in paths)
            {
                ct.ThrowIfCancellationRequested();
                if (Directory.Exists(path))
                {
                    var files = EnumerateFolder(path).ToList();
                    FilesFound += files.Count;
                    foreach (var file in files)
                    {
                        await ProcessFileAsync(file).ConfigureAwait(false);
                    }
                }
                else if (File.Exists(path))
                {
                    FilesFound++;
                    await ProcessFileAsync(path).ConfigureAwait(false);
                }
            }
        }

        private static IEnumerable<string> EnumerateFolder(string folder)
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System };
            return Directory.EnumerateFiles(folder, "*", options).Where(IsSupported).Order(StringComparer.OrdinalIgnoreCase);
        }

        private async Task ProcessFileAsync(string path)
        {
            ct.ThrowIfCancellationRequested();
            var ext = Path.GetExtension(path).ToLowerInvariant();
            try
            {
                if (ext == ".zip")
                {
                    FilesFound--; // das ZIP selbst zählt nicht, nur seine Einträge
                    await using var zipStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
                    await ProcessZipAsync(zipStream, 1).ConfigureAwait(false);
                    return;
                }

                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
                await ProcessTrackAsync(stream, ext).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                // Kaputte bzw. fremde Datei: zählen und weiter — eine einzelne Datei bricht das Einlesen nie ab
                Failed++;
                owner._logger.LogDebug("GPS-Spuren: Datei nicht lesbar ({Type})", ex.GetType().Name); // ohne Namen (Datenschutz)
            }
        }

        private async Task ProcessZipAsync(Stream stream, int depth)
        {
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            var entries = zip.Entries.Where(e => e.Length > 0 && IsSupported(e.FullName)).ToList();
            FilesFound += entries.Count(e => !e.FullName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                var ext = Path.GetExtension(entry.FullName).ToLowerInvariant();
                try
                {
                    if (ext == ".zip")
                    {
                        if (depth >= MaxZipDepth || entry.Length > MaxNestedZipBytes)
                        {
                            continue;
                        }

                        var temp = Path.Combine(EnsureTemp(), Guid.NewGuid().ToString("N") + ".zip");
                        try
                        {
                            await using (var source = entry.Open())
                            await using (var target = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920))
                            {
                                await CopyBoundedAsync(source, target, MaxNestedZipBytes).ConfigureAwait(false);
                            }

                            await using var nested = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
                            await ProcessZipAsync(nested, depth + 1).ConfigureAwait(false);
                        }
                        finally
                        {
                            TryDelete(temp);
                        }

                        continue;
                    }

                    if (entry.Length > FitTrackReader.MaxFileBytes)
                    {
                        Failed++;
                        continue;
                    }

                    using var memory = new MemoryStream((int)Math.Max(0, Math.Min(entry.Length, FitTrackReader.MaxFileBytes)));
                    await using (var source = entry.Open())
                    {
                        await CopyBoundedAsync(source, memory, FitTrackReader.MaxFileBytes).ConfigureAwait(false);
                    }

                    memory.Position = 0;
                    await ProcessTrackAsync(memory, ext).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                {
                    Failed++;
                    owner._logger.LogDebug("GPS-Spuren: ZIP-Eintrag nicht lesbar ({Type})", ex.GetType().Name);
                }
            }
        }

        private async Task CopyBoundedAsync(Stream source, Stream target, long max)
        {
            var buffer = new byte[81920];
            long total = 0;
            int n;
            while ((n = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                total += n;
                if (total > max)
                {
                    throw new InvalidDataException("Eintrag größer als angegeben.");
                }

                await target.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            }
        }

        private async Task ProcessTrackAsync(Stream stream, string ext)
        {
            FilesRead++;
            IReadOnlyList<ParsedTrack> tracks = ext switch
            {
                ".fit" => FitTrackReader.Read(stream) is { } t ? [t] : [],
                ".gpx" => XmlTrackReader.ReadGpx(stream),
                ".tcx" => XmlTrackReader.ReadTcx(stream),
                _ => [],
            };

            if (tracks.Count == 0)
            {
                WithoutGps++;
            }

            foreach (var track in tracks)
            {
                var checksum = TrackCodec.Checksum(track.Points);
                if (!known.Add(GpsTrackStore.Hex(checksum)))
                {
                    Duplicates++;
                    continue;
                }

                _buffer.Add(new NewGpsTrack(track, checksum));
                if (_buffer.Count >= InsertBatchSize)
                {
                    await FlushAsync().ConfigureAwait(false);
                }
            }

            owner.PublishStatus(Status(true));
        }

        public async Task FlushAsync(CancellationToken? token = null)
        {
            if (_buffer.Count == 0)
            {
                return;
            }

            var batch = _buffer.ToList();
            _buffer.Clear();
            var inserted = await owner._store.InsertAsync(groupId, batch, token ?? ct).ConfigureAwait(false);
            Imported += inserted;
            Duplicates += batch.Count - inserted;
        }

        private string EnsureTemp()
        {
            if (_tempDir is null)
            {
                _tempDir = Path.Combine(owner._storage.TempRoot, "gps-import-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(_tempDir);
            }

            return _tempDir;
        }

        public void DeleteTemp()
        {
            if (_tempDir is null)
            {
                return;
            }

            try
            {
                Directory.Delete(_tempDir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                owner._logger.LogDebug("GPS-Spuren: Temp-Ordner nicht gelöscht ({Type})", ex.GetType().Name);
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // bleibt im Temp-Ordner des Katalogs, der am Ende gelöscht wird
            }
        }
    }
}
