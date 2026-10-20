using CommunityToolkit.Mvvm.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PhotoViewerV3.Domain.Enums;
using PhotoViewerV3.Services.Database;
using PhotoViewerV3.Services.Messaging;
using PhotoViewerV3.Services.Operations;
using PhotoViewerV3.Services.Scanning;
using PhotoViewerV3.Services.Watch;

namespace PhotoViewerV3.Services.Archive;

/// <summary>
/// Produktive Implementierung von <see cref="IArchiveAutoExtractService"/> (ADR-0032). Setzt außerhalb des ScanService an:
/// beobachtet die Scan-Sperre (<see cref="IScanGate.Acquired"/>) und die Nachrichten des Scans, reiht nach einem
/// Incremental-Scan einen Lauf ein und arbeitet ihn auf dem Thread-Pool ab.
/// <para>
/// Sperren-Reihenfolge (kein Deadlock, die Sperre ist nicht wiedereintrittsfähig): die Nachrichten-Handler laufen
/// synchron auf dem Scan-Thread, während der Scan die Sperre hält — sie merken sich nur Ids und reihen ein. Der Lauf
/// selbst belegt die Sperre je Archiv nur für das Entpacken (<see cref="IScanGate.AcquireForFileOperationAsync"/>, wartet
/// also auf das Ende des Scans) und gibt sie vor Papierkorb (FileOperationService holt sie selbst) und Folge-Scan
/// (ScanService holt sie selbst) wieder frei.
/// </para>
/// <para>
/// Keine Endlosschleife: der Folge-Scan nimmt die entpackten Dateien auf und reiht wieder einen Lauf ein; Archive darin
/// liegen in einem gemerkten Zielordner (<see cref="ArchiveExtractedMessage"/>) und werden übersprungen. Ein Archiv, dessen
/// Zielordner schon existiert (wiederhergestellt, schon einmal entpackt), bleibt unberührt.
/// </para>
/// </summary>
public sealed class ArchiveAutoExtractService : IArchiveAutoExtractService
{
    private const int IdChunkSize = 500;

    private readonly ILibraryContextFactory _factory;
    private readonly IArchiveAutoExtractSettingsStore _store;
    private readonly IArchiveService _archives;
    private readonly IScanService _scanner;
    private readonly IFileOperationService _fileOps;
    private readonly IScanGate _scanGate;
    private readonly IOwnChangeRegistry _ownChanges;
    private readonly IMessenger _messenger;
    private readonly ILogger<ArchiveAutoExtractService> _logger;

    private readonly object _sync = new();
    private readonly CancellationTokenSource _cts = new();

    /// <summary>Neu aufgenommene Ids je Bibliothek, solange deren Scan läuft (zwischen Acquired und Completed/Aborted).</summary>
    private readonly Dictionary<int, HashSet<int>> _collecting = new();

    /// <summary>Ordner, die aus einem Archiv entstanden sind (normalisiert) — Archive darin werden nie automatisch entpackt.</summary>
    private readonly HashSet<string> _producedFolders = new(StringComparer.OrdinalIgnoreCase);

    private Task _tail = Task.CompletedTask;
    private volatile bool _enabled;
    private volatile bool _recycle;
    private volatile ArchiveAutoExtractSummary? _lastSummary;
    private bool _started;
    private bool _disposed;

    /// <param name="messenger">Optional (Tests); produktiv WeakReferenceMessenger.Default.</param>
    public ArchiveAutoExtractService(
        ILibraryContextFactory factory,
        IArchiveAutoExtractSettingsStore store,
        IArchiveService archives,
        IScanService scanner,
        IFileOperationService fileOps,
        IScanGate scanGate,
        IOwnChangeRegistry ownChanges,
        ILogger<ArchiveAutoExtractService> logger,
        IMessenger? messenger = null)
    {
        _factory = factory;
        _store = store;
        _archives = archives;
        _scanner = scanner;
        _fileOps = fileOps;
        _scanGate = scanGate;
        _ownChanges = ownChanges;
        _logger = logger;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
    }

    public bool IsEnabled => _enabled;

    public bool RecycleAfterExtract => _recycle;

    public ArchiveAutoExtractSummary? LastSummary => _lastSummary;

    public async Task StartAsync(CancellationToken ct = default)
    {
        var settings = await _store.LoadAsync(ct).ConfigureAwait(false);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _enabled = settings.Enabled;
            _recycle = settings.RecycleAfterExtract;
            if (_started)
            {
                return;
            }

            _started = true;
        }

        _scanGate.Acquired += OnScanGateAcquired;
        _messenger.Register<ArchiveAutoExtractService, FilesChangedMessage>(this, static (r, m) => r.OnFilesChanged(m));
        _messenger.Register<ArchiveAutoExtractService, ScanCompletedMessage>(this, static (r, m) => r.OnScanCompleted(m));
        _messenger.Register<ArchiveAutoExtractService, ScanAbortedMessage>(this, static (r, m) => r.OnScanAborted(m));
        _messenger.Register<ArchiveAutoExtractService, ArchiveExtractedMessage>(this, static (r, m) => r.OnArchiveExtracted(m));
        _logger.LogInformation("Automatisches Entpacken: {State}, Papierkorb danach: {Recycle}", settings.Enabled ? "an" : "aus", settings.RecycleAfterExtract ? "ja" : "nein");
    }

    public async Task SetEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        await _store.SaveAsync(new ArchiveAutoExtractSettings(enabled, _recycle), ct).ConfigureAwait(false);
        lock (_sync)
        {
            _enabled = enabled;
            if (!enabled)
            {
                _collecting.Clear();
            }
        }

        _logger.LogInformation("Automatisches Entpacken {State}", enabled ? "eingeschaltet" : "ausgeschaltet");
    }

    public async Task SetRecycleAfterExtractAsync(bool recycle, CancellationToken ct = default)
    {
        await _store.SaveAsync(new ArchiveAutoExtractSettings(_enabled, recycle), ct).ConfigureAwait(false);
        _recycle = recycle;
        _logger.LogInformation("Automatisches Entpacken: Papierkorb danach {State}", recycle ? "an" : "aus");
    }

    public async Task WhenIdleAsync()
    {
        while (true)
        {
            Task tail;
            lock (_sync)
            {
                tail = _tail;
            }

            await tail.ConfigureAwait(false);
            lock (_sync)
            {
                if (ReferenceEquals(tail, _tail))
                {
                    return;
                }
            }
        }
    }

    // ---- Beobachter (laufen synchron auf dem Scan-Thread, während der Scan die Sperre hält: nur merken) -----------

    private void OnScanGateAcquired(int libraryId)
    {
        lock (_sync)
        {
            if (_enabled && !_disposed)
            {
                _collecting[libraryId] = new HashSet<int>();
            }
            else
            {
                _collecting.Remove(libraryId);
            }
        }
    }

    private void OnFilesChanged(FilesChangedMessage message)
    {
        if (message.Kind != FilesChangeKind.Added || message.FileIds.Count == 0)
        {
            return;
        }

        lock (_sync)
        {
            if (_collecting.TryGetValue(message.LibraryId, out var ids))
            {
                ids.UnionWith(message.FileIds);
            }
        }
    }

    private void OnScanCompleted(ScanCompletedMessage message)
    {
        List<int>? ids = null;
        lock (_sync)
        {
            if (_collecting.Remove(message.LibraryId, out var collected)
                && collected.Count > 0
                && _enabled
                && !_disposed
                && message.Result.Mode == ScanMode.Incremental)
            {
                ids = collected.ToList();
            }
        }

        if (ids is not null)
        {
            Enqueue(message.LibraryId, ids);
        }
    }

    private void OnScanAborted(ScanAbortedMessage message)
    {
        lock (_sync)
        {
            _collecting.Remove(message.LibraryId); // abgebrochen/offline/fehlgeschlagen: nichts anfassen
        }
    }

    private void OnArchiveExtracted(ArchiveExtractedMessage message)
    {
        RememberProducedFolder(message.TargetFolder);
    }

    private void RememberProducedFolder(string folder)
    {
        var key = OwnChangeRegistry.Normalize(folder);
        if (key is null)
        {
            return;
        }

        lock (_sync)
        {
            _producedFolders.Add(key);
        }
    }

    private bool IsInProducedFolder(string path)
    {
        var key = OwnChangeRegistry.Normalize(path);
        lock (_sync)
        {
            for (var folder = Path.GetDirectoryName(key); !string.IsNullOrEmpty(folder); folder = Path.GetDirectoryName(folder))
            {
                if (_producedFolders.Contains(folder))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private void Enqueue(int libraryId, IReadOnlyList<int> ids)
    {
        var token = _cts.Token;
        lock (_sync)
        {
            _tail = _tail
                .ContinueWith(_ => RunSafeAsync(libraryId, ids, token), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default)
                .Unwrap();
        }
    }

    // ---- Lauf --------------------------------------------------------------------------------------

    private async Task RunSafeAsync(int libraryId, IReadOnlyList<int> ids, CancellationToken ct)
    {
        try
        {
            await RunAsync(libraryId, ids, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // App wird beendet
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Automatisches Entpacken fehlgeschlagen: Library={LibraryId}", libraryId);
        }
    }

    private async Task RunAsync(int libraryId, IReadOnlyList<int> ids, CancellationToken ct)
    {
        if (!_enabled || ct.IsCancellationRequested)
        {
            return;
        }

        var candidates = new List<Candidate>();
        foreach (var candidate in await LoadCandidatesAsync(libraryId, ids, ct).ConfigureAwait(false))
        {
            if (IsInProducedFolder(candidate.Path))
            {
                _logger.LogInformation("Automatisches Entpacken: {Path} liegt in einem entpackten Ordner — nicht angefasst (verschachtelte Archive nur über das Runden-System)", candidate.Path);
                continue;
            }

            candidates.Add(candidate);
        }

        if (candidates.Count == 0)
        {
            return;
        }

        _logger.LogInformation("Automatisches Entpacken: {Count} neue(s) Archiv(e) in Eingangsordnern der Bibliothek {LibraryId}", candidates.Count, libraryId);
        var items = new List<ArchiveAutoExtractItem>(candidates.Count);
        ScanResult? scan = null;
        string? scanError = null;
        var canceled = false;

        // Ein Scope für den ganzen Lauf: die Ordnerbäume gelten bis nach dem Folge-Scan (plus Nachlaufzeit) als eigene
        // Änderungen — die Ordnerüberwachung löst dafür keinen zweiten Scan aus.
        using (var own = _ownChanges.Begin())
        {
            try
            {
                for (var i = 0; i < candidates.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!_enabled)
                    {
                        _logger.LogInformation("Automatisches Entpacken ausgeschaltet — {Left} Archiv(e) bleiben unberührt", candidates.Count - i);
                        break;
                    }

                    Publish(new ArchiveAutoExtractStatus(libraryId, ArchiveAutoExtractState.Running, i + 1, candidates.Count, candidates[i].Name));
                    items.Add(await ProcessOneAsync(libraryId, candidates[i], own, ct).ConfigureAwait(false));
                }

                if (items.Any(i => i.FilesExtracted > 0 || i.Recycled))
                {
                    Publish(new ArchiveAutoExtractStatus(libraryId, ArchiveAutoExtractState.Scanning, items.Count, candidates.Count, null));
                    scan = await _scanner.ScanAsync(libraryId, ScanMode.Incremental, null, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                canceled = true;
            }
            catch (Exception ex)
            {
                scanError = ex.Message;
                _logger.LogWarning(ex, "Automatisches Entpacken: Bibliothek {LibraryId} konnte danach nicht aktualisiert werden", libraryId);
            }
        }

        var summary = new ArchiveAutoExtractSummary(libraryId, items, scan, scanError, canceled);
        _lastSummary = summary;
        _logger.LogInformation(
            "Automatisches Entpacken fertig: Library={LibraryId} — {Extracted}/{Total} entpackt, {Files} Dateien, {Recycled} im Papierkorb, {Problems} gemeldet, Scan={Scan}, abgebrochen={Canceled}",
            libraryId, summary.ExtractedCount, candidates.Count, summary.FilesExtracted, summary.RecycledCount, summary.Problems.Count,
            scan is null ? (scanError ?? "-") : $"{scan.Added} neu", canceled);
        Publish(new ArchiveAutoExtractStatus(libraryId, ArchiveAutoExtractState.Finished, items.Count, candidates.Count, null, summary));
    }

    private async Task<ArchiveAutoExtractItem> ProcessOneAsync(int libraryId, Candidate candidate, IOwnChangeScope own, CancellationToken ct)
    {
        if (!File.Exists(candidate.Path))
        {
            return Item(candidate, ArchiveAutoExtractItemState.Skipped, null, "Archiv nicht mehr vorhanden.");
        }

        var target = Path.GetFullPath(_archives.GetDefaultTargetFolder(candidate.Path));
        if (Directory.Exists(target) || File.Exists(target))
        {
            return Item(candidate, ArchiveAutoExtractItemState.Skipped, target, $"Zielordner „{Path.GetFileName(target)}“ existiert bereits — nicht automatisch entpackt.");
        }

        // Vor dem ersten Byte: Ordner merken (Archive darin nie automatisch) und für die Ordnerüberwachung als eigen melden.
        RememberProducedFolder(target);
        own.AddTree(target);

        ArchiveExtractResult result;
        try
        {
            // Nie während eines Scans (oder Datei-Batches) derselben Bibliothek entpacken: Sperre wie eine Datei-Operation
            // belegen — nur für das Entpacken; Papierkorb und Scan holen sie danach selbst.
            using (await _scanGate.AcquireForFileOperationAsync(libraryId, ct).ConfigureAwait(false))
            {
                result = await _archives.ExtractAsync(candidate.Path, new ArchiveExtractOptions
                {
                    TargetFolder = target,
                    OverwriteMode = ArchiveOverwriteMode.Skip,
                    MediaOnly = false, // alles entpacken: was nur im Archiv lag, geht mit dem Papierkorb nicht verloren
                    ExtractNested = true,
                    RecycleArchiveAfterExtract = false,
                    ArchiveFileId = null,
                    LibraryId = null,
                    ScanAfterExtract = false,
                }, null, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is ArchiveEncryptedException or ArchiveFormatException)
        {
            _logger.LogWarning("Automatisches Entpacken: {Path} nicht entpackt — {Reason}", candidate.Path, ex.Message);
            TryDeleteEmptyFolder(target);
            return Item(candidate, ArchiveAutoExtractItemState.Failed, null, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Automatisches Entpacken: {Path} nicht entpackt", candidate.Path);
            TryDeleteEmptyFolder(target);
            return Item(candidate, ArchiveAutoExtractItemState.Failed, null, $"Nicht entpackt: {ex.Message}");
        }

        if (result.Canceled)
        {
            ct.ThrowIfCancellationRequested();
            return Item(candidate, ArchiveAutoExtractItemState.Failed, target, "Entpacken abgebrochen.", result.FilesExtracted);
        }

        if (result.FilesExtracted == 0)
        {
            TryDeleteEmptyFolder(target);
            var reason = result.HasErrors ? FirstErrors(result) : "Das Archiv enthält keine Dateien.";
            return Item(candidate, ArchiveAutoExtractItemState.Failed, null, reason);
        }

        if (result.HasErrors)
        {
            return Item(candidate, ArchiveAutoExtractItemState.ExtractedWithErrors, target,
                FirstErrors(result) + (_recycle ? " — Archiv bleibt erhalten." : string.Empty), result.FilesExtracted);
        }

        if (!_recycle)
        {
            return Item(candidate, ArchiveAutoExtractItemState.Extracted, target, null, result.FilesExtracted);
        }

        // Papierkorb protokolliert über IFileOperationService (ADR-0012/0018) — holt die Sperre selbst.
        string? recycleError;
        try
        {
            var batch = await _fileOps.RecycleAsync([candidate.FileId], ct).ConfigureAwait(false);
            if (batch.Succeeded == 1)
            {
                return Item(candidate, ArchiveAutoExtractItemState.Extracted, target, null, result.FilesExtracted, recycled: true);
            }

            recycleError = batch.Items.FirstOrDefault()?.Error ?? "unbekannter Fehler";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            recycleError = ex.Message;
        }

        _logger.LogWarning("Automatisches Entpacken: {Path} entpackt, aber nicht in den Papierkorb verschoben — {Reason}", candidate.Path, recycleError);
        return Item(candidate, ArchiveAutoExtractItemState.Extracted, target, $"Nicht in den Papierkorb verschoben: {recycleError}", result.FilesExtracted);
    }

    private static string FirstErrors(ArchiveExtractResult result)
    {
        var first = result.Errors.Take(2).Select(e => $"{e.Entry}: {e.Message}");
        var more = result.Errors.Count > 2 ? $" (+{result.Errors.Count - 2} weitere)" : string.Empty;
        return string.Join("; ", first) + more;
    }

    private static ArchiveAutoExtractItem Item(
        Candidate candidate,
        ArchiveAutoExtractItemState state,
        string? target,
        string? message,
        int files = 0,
        bool recycled = false)
        => new(candidate.FileId, candidate.Path, state, target, files, recycled, message);

    private void TryDeleteEmptyFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
            {
                Directory.Delete(folder);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Leerer Zielordner konnte nicht entfernt werden: {Folder}", folder);
        }
    }

    /// <summary>Archive (nicht fehlend) unter Eingangs-Wurzeln der Bibliothek aus den neu aufgenommenen Ids, nach Pfad sortiert.</summary>
    private async Task<List<Candidate>> LoadCandidatesAsync(int libraryId, IReadOnlyList<int> ids, CancellationToken ct)
    {
        var result = new List<Candidate>();
        await using var ctx = _factory.Create();
        foreach (var chunk in ids.Chunk(IdChunkSize))
        {
            var rows = await (
                    from f in ctx.Files.AsNoTracking()
                    join d in ctx.Folders.AsNoTracking() on f.FolderId equals d.Id
                    join r in ctx.LibraryRoots.AsNoTracking() on d.RootId equals (int?)r.Id
                    where chunk.Contains(f.Id)
                          && f.LibraryId == libraryId
                          && f.MediaType == MediaType.Archive
                          && !f.Missing
                          && r.LibraryId == libraryId
                          && r.Role == LibraryRootRole.Inbox
                    select new { f.Id, FolderPath = d.Path, f.Name })
                .ToListAsync(ct)
                .ConfigureAwait(false);
            result.AddRange(rows.Select(r => new Candidate(r.Id, Path.Combine(r.FolderPath, r.Name), r.Name)));
        }

        result.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Path, b.Path));
        return result;
    }

    private void Publish(ArchiveAutoExtractStatus status)
    {
        try
        {
            _messenger.Send(new ArchiveAutoExtractStatusMessage(status));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Empfänger der ArchiveAutoExtractStatusMessage warf eine Ausnahme");
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _collecting.Clear();
        }

        _cts.Cancel();
        _scanGate.Acquired -= OnScanGateAcquired;
        _messenger.UnregisterAll(this);
    }

    private sealed record Candidate(int FileId, string Path, string Name);
}
