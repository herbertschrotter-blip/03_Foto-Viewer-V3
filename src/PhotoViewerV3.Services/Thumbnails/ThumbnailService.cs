using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PhotoViewerV3.Domain.Entities;
using PhotoViewerV3.Domain.Enums;
using PhotoViewerV3.Services.Database;
using PhotoViewerV3.Services.Infrastructure;

namespace PhotoViewerV3.Services.Thumbnails;

/// <summary>
/// Produktiver Thumbnail-Motor: zwei Hintergrund-Worker-Pools (Bild via WIC,
/// Video via FFmpeg) lesen aus getrennten Channels, dedupliziert über einen
/// In-Memory-State. DB-Status (Pending → Generated/Failed) wird serialisiert
/// geschrieben (Single-Writer via SemaphoreSlim, gegen SQLITE_BUSY). Atomic
/// .tmp+Move liegt in den Renderern. Siehe ADR-0007.
/// Bilder, die WIC nicht decodieren kann (ErrorCode <c>wic_decode_failed</c>), bekommen einen
/// zweiten Versuch über den optionalen <see cref="IFallbackImageThumbnailRenderer"/> (FFmpeg).
/// In-Memory-State: je FileId Ergebnis + <c>SourceModifiedAtUtc</c> der gerenderten Version. Ein
/// Request mit anderem Zeitstempel (Datei geändert, FileId nach Papierkorb/Rescan wiederverwendet)
/// verwirft den Eintrag und prüft neu; ein Done-Eintrag ohne Cache-Datei ebenso. Fehlschläge
/// bleiben für die Session gesperrt (kein Hämmern), außer bei „äußeren“ Ursachen
/// (<see cref="ThumbnailErrorCodes.SourceMissing"/>, <see cref="ThumbnailErrorCodes.FfmpegNotFound"/>):
/// die verbrauchen keinen <c>AttemptCount</c> und werden nach <see cref="RetryCooldown"/> erneut versucht.
/// </summary>
public sealed class ThumbnailService : IThumbnailService, IDisposable
{
    public const int Size = 320;

    /// <summary>Nach so vielen gescheiterten Versuchen wird eine Datei nicht mehr eingereiht.</summary>
    public const int MaxAttempts = 3;

    /// <summary>Nach dieser Zeit wird ein Fehlschlag mit äußerer Ursache (Quelle fehlt, FFmpeg fehlt) erneut versucht.</summary>
    public static readonly TimeSpan RetryCooldown = TimeSpan.FromMinutes(5);

    /// <summary>.tmp-Leichen, die älter sind, gehören keinem laufenden Job mehr und werden beim Start entfernt.</summary>
    public static readonly TimeSpan TempFileMaxAge = TimeSpan.FromMinutes(2);

    private const int ErrorMessageMaxLen = 500;

    private readonly ILibraryContextFactory _factory;
    private readonly IAppDataPaths _paths;
    private readonly IImageThumbnailRenderer _imageRenderer;
    private readonly IVideoThumbnailRenderer _videoRenderer;
    private readonly IFallbackImageThumbnailRenderer? _fallbackRenderer;
    private readonly ILogger<ThumbnailService> _logger;

    private readonly Channel<ThumbnailRequest> _imageChannel =
        Channel.CreateUnbounded<ThumbnailRequest>(new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });
    private readonly Channel<ThumbnailRequest> _videoChannel =
        Channel.CreateUnbounded<ThumbnailRequest>(new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });

    private readonly ConcurrentDictionary<int, JobState> _states = new();
    private readonly SemaphoreSlim _dbWriteLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _workers = new();
    private int _started;
    private int _pending;
    private int _disposed;
    private int _ffmpegMissingLogged;
    private readonly object _pauseLock = new();
    private TaskCompletionSource? _resumed; // gesetzt, solange pausiert (ADR-0026)
    private readonly Dictionary<int, ThumbnailRequest> _deferred = new(); // während der Pause angefordert; unter _pauseLock
    private int _activeJobs;

    private enum JobOutcome { Queued, Done, Failed }

    /// <summary>Zustand je FileId; <c>Retryable</c>-Fehlschläge dürfen nach <see cref="RetryCooldown"/> erneut laufen.</summary>
    private readonly record struct JobState(JobOutcome Outcome, DateTime SourceModifiedAtUtc, DateTime AtUtc, bool Retryable);

    public ThumbnailService(
        ILibraryContextFactory factory,
        IAppDataPaths paths,
        IImageThumbnailRenderer imageRenderer,
        IVideoThumbnailRenderer videoRenderer,
        ILogger<ThumbnailService> logger,
        IFallbackImageThumbnailRenderer? fallbackRenderer = null)
    {
        _factory = factory;
        _paths = paths;
        _imageRenderer = imageRenderer;
        _videoRenderer = videoRenderer;
        _fallbackRenderer = fallbackRenderer;
        _logger = logger;
    }

    public int ThumbnailSize => Size;

    public int PendingCount => Volatile.Read(ref _pending);

    public event Action<ThumbnailReadyEventArgs>? ThumbnailReady;

    public void Invalidate(IReadOnlyList<int> fileIds)
    {
        ArgumentNullException.ThrowIfNull(fileIds);
        foreach (var id in fileIds)
        {
            // Laufende Jobs behalten ihren Eintrag (Dedupe); der nächste Request validiert ihn ohnehin.
            if (_states.TryGetValue(id, out var state) && state.Outcome != JobOutcome.Queued)
            {
                _states.TryRemove(new KeyValuePair<int, JobState>(id, state));
            }
        }
    }

    public async Task PauseAsync(CancellationToken ct = default)
    {
        lock (_pauseLock)
        {
            _resumed ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        while (Volatile.Read(ref _activeJobs) > 0)
        {
            await Task.Delay(25, ct).ConfigureAwait(false);
        }

        _logger.LogInformation("ThumbnailService pausiert ({Pending} Aufträge warten)", PendingCount);
    }

    public void Resume()
    {
        TaskCompletionSource? resumed;
        List<ThumbnailRequest> deferred;
        lock (_pauseLock)
        {
            resumed = _resumed;
            _resumed = null;
            deferred = [.. _deferred.Values];
            _deferred.Clear();
        }

        if (resumed is not null)
        {
            resumed.TrySetResult();
            _logger.LogInformation("ThumbnailService fortgesetzt ({Deferred} zurückgestellte Anforderungen)", deferred.Count);
        }

        if (deferred.Count > 0)
        {
            // Nach der Rücknahme eines Umzugs sofort nachholen — nicht erst mit dem nächsten Seitenwechsel der Galerie.
            _ = Task.Run(() => ResubmitDeferredAsync(deferred));
        }
    }

    private async Task ResubmitDeferredAsync(IReadOnlyList<ThumbnailRequest> deferred)
    {
        try
        {
            await RequestThumbnailsAsync(deferred).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Zurückgestellte Thumbnail-Anforderungen konnten nicht nachgeholt werden");
        }
    }

    /// <summary>
    /// Pausiert: Anforderungen je FileId (die neueste gewinnt) zurückstellen statt sie zu verwerfen; <see cref="Resume"/>
    /// reicht sie nach. Unter derselben Sperre wie <see cref="Resume"/>, damit keine zwischen Prüfen und Merken verloren geht.
    /// </summary>
    private bool TryDefer(IReadOnlyList<ThumbnailRequest> requests)
    {
        lock (_pauseLock)
        {
            if (_resumed is null)
            {
                return false;
            }

            foreach (var req in requests)
            {
                if (req.MediaType != MediaType.Archive)
                {
                    _deferred[req.FileId] = req;
                }
            }

            return true;
        }
    }

    /// <summary>Belegt einen Job-Platz; ist der Dienst pausiert, gibt er ihn wieder frei und wartet auf <see cref="Resume"/>.</summary>
    private async Task EnterJobAsync(CancellationToken token)
    {
        while (true)
        {
            Interlocked.Increment(ref _activeJobs);
            Task? resumed;
            lock (_pauseLock)
            {
                resumed = _resumed?.Task;
            }

            if (resumed is null)
            {
                return;
            }

            Interlocked.Decrement(ref _activeJobs);
            await resumed.WaitAsync(token).ConfigureAwait(false);
        }
    }

    public void InvalidateAll()
    {
        // ConcurrentDictionary erlaubt Entfernen während der Enumeration; laufende Jobs bleiben (Dedupe).
        foreach (var (id, state) in _states)
        {
            if (state.Outcome != JobOutcome.Queued)
            {
                _states.TryRemove(new KeyValuePair<int, JobState>(id, state));
            }
        }
    }

    public async Task RequestThumbnailsAsync(
        IReadOnlyList<ThumbnailRequest> requests,
        CancellationToken cancellationToken = default)
    {
        if (requests.Count == 0 || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        if (TryDefer(requests))
        {
            // Katalog-DB zieht um (ADR-0026): sie ist gesperrt. Nach einer Rücknahme reicht Resume die Anforderungen
            // nach; nach Erfolg startet die App neu und die Galerie fordert ohnehin neu an.
            _logger.LogDebug("ThumbnailService pausiert — {Count} Anforderungen zurückgestellt", requests.Count);
            return;
        }

        EnsureStarted();

        // 1) Schon bekannte Files direkt behandeln, Rest sammeln.
        var toCheck = new List<ThumbnailRequest>(requests.Count);
        foreach (var req in requests)
        {
            // Archive (ADR-0022) haben kein Vorschaubild: weder DB-Zeile noch Renderer noch Event — die Kachel zeigt ein Icon.
            if (req.MediaType == MediaType.Archive)
            {
                continue;
            }

            if (TryHandleKnown(req))
            {
                continue;
            }

            toCheck.Add(req);
        }

        if (toCheck.Count == 0)
        {
            return;
        }

        // 2) Batch-DB-Check: existierende Thumbnail-Einträge für diese FileIds (ein Query).
        var ids = toCheck.Select(r => r.FileId).Distinct().ToList();
        Dictionary<int, Thumbnail> existing;
        try
        {
            await using var ctx = _factory.Create();
            existing = await ctx.Thumbnails
                .AsNoTracking()
                .Where(t => t.Size == Size && t.Kind == ThumbnailKind.Still && ids.Contains(t.FileId))
                .ToDictionaryAsync(t => t.FileId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Thumbnail-Batch-Check fehlgeschlagen");
            existing = new Dictionary<int, Thumbnail>();
        }

        // 3) Pro Request entscheiden: cache-hit, skip (zu oft gescheitert / kein FFmpeg) oder enqueue.
        foreach (var req in toCheck)
        {
            existing.TryGetValue(req.FileId, out var row);

            var cacheHit = row is { Status: ThumbnailStatus.Generated }
                && row.SourceModifiedAtUtc == req.SourceModifiedAtUtc
                && File.Exists(row.CachePath);
            if (cacheHit)
            {
                SetState(req, JobOutcome.Done);
                RaiseReady(new ThumbnailReadyEventArgs(req.LibraryId, req.FileId, row!.CachePath));
                continue;
            }

            if (row is { Status: ThumbnailStatus.Failed } && row.AttemptCount >= MaxAttempts)
            {
                SetState(req, JobOutcome.Failed);
                continue;
            }

            if (req.MediaType == MediaType.Video && !_videoRenderer.IsAvailable)
            {
                if (Interlocked.Exchange(ref _ffmpegMissingLogged, 1) == 0)
                {
                    _logger.LogWarning("FFmpeg nicht gefunden — Video-Thumbnails werden in dieser Sitzung übersprungen");
                }

                SetState(req, JobOutcome.Failed);
                continue;
            }

            if (_states.TryAdd(req.FileId, new JobState(JobOutcome.Queued, req.SourceModifiedAtUtc, DateTime.UtcNow, false)))
            {
                var channel = req.MediaType == MediaType.Video ? _videoChannel : _imageChannel;
                if (channel.Writer.TryWrite(req))
                {
                    Interlocked.Increment(ref _pending);
                }
                else
                {
                    _states.TryRemove(req.FileId, out _);
                }
            }
        }
    }

    /// <summary>
    /// Behandelt einen Request über den In-Memory-State. True = erledigt (Event gefeuert, läuft schon
    /// oder bewusst übersprungen); false = Eintrag fehlt/ungültig → DB-Prüfung nötig.
    /// </summary>
    private bool TryHandleKnown(ThumbnailRequest req)
    {
        if (!_states.TryGetValue(req.FileId, out var state))
        {
            return false;
        }

        if (state.Outcome == JobOutcome.Queued)
        {
            return true; // in Arbeit — auch für eine andere Version: der nächste Request validiert neu
        }

        if (state.SourceModifiedAtUtc != req.SourceModifiedAtUtc)
        {
            // Datei geändert oder FileId wiederverwendet (Papierkorb/Rescan) → Zustand verwerfen.
            _states.TryRemove(new KeyValuePair<int, JobState>(req.FileId, state));
            return false;
        }

        if (state.Outcome == JobOutcome.Done)
        {
            var path = BuildCachePath(req.LibraryId, req.FileId);
            if (File.Exists(path))
            {
                RaiseReady(new ThumbnailReadyEventArgs(req.LibraryId, req.FileId, path));
                return true;
            }

            // Cache-Datei fehlt (gelöscht, Cache geleert) → neu rendern.
            _states.TryRemove(new KeyValuePair<int, JobState>(req.FileId, state));
            return false;
        }

        // Failed: für die Session gesperrt — außer äußere Ursache und Cooldown abgelaufen.
        if (state.Retryable && DateTime.UtcNow - state.AtUtc >= RetryCooldown)
        {
            _states.TryRemove(new KeyValuePair<int, JobState>(req.FileId, state));
            return false;
        }

        return true;
    }

    private void SetState(ThumbnailRequest req, JobOutcome outcome, bool retryable = false)
        => _states[req.FileId] = new JobState(outcome, req.SourceModifiedAtUtc, DateTime.UtcNow, retryable);

    private void EnsureStarted()
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
        {
            return;
        }

        // UI-Reaktion vor Durchsatz: höchstens die Hälfte der logischen Kerne, maximal 6 (PLAN Kap. 11 nennt
        // CPU-2 für SSD-Profile; damit war der UI-Thread beim Erstaufbau von 500 großen JPEGs ausgehungert).
        var imageWorkers = Math.Clamp(Environment.ProcessorCount / 2, 2, 6);
        for (var i = 0; i < imageWorkers; i++)
        {
            _workers.Add(Task.Run(() => WorkerLoopAsync(_imageChannel.Reader)));
        }

        // Video: max. 1 Worker (FFmpeg ist teuer, ADR-0007).
        _workers.Add(Task.Run(() => WorkerLoopAsync(_videoChannel.Reader)));

        // .tmp-Leichen aus abgestürzten Sitzungen entfernen (nur alte, damit laufende Jobs unberührt bleiben).
        _ = Task.Run(CleanupTempFiles);

        _logger.LogInformation(
            "ThumbnailService gestartet: {ImageWorkers} Bild-Worker + 1 Video-Worker (FFmpeg verfügbar: {Ffmpeg})",
            imageWorkers, _videoRenderer.IsAvailable);
    }

    /// <summary>Worker-Schleife: Ausnahmen werden geloggt, der Worker läuft weiter (Pool schrumpft nicht); Ende nur bei Shutdown.</summary>
    private async Task WorkerLoopAsync(ChannelReader<ThumbnailRequest> reader)
    {
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            ThumbnailRequest? current = null;
            try
            {
                if (!await reader.WaitToReadAsync(token).ConfigureAwait(false))
                {
                    return; // Channel abgeschlossen
                }

                while (reader.TryRead(out var req))
                {
                    current = req;
                    await EnterJobAsync(token).ConfigureAwait(false);
                    try
                    {
                        await ProcessJobAsync(req).ConfigureAwait(false);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _activeJobs);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return; // Shutdown
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Thumbnail-Worker: unerwartete Ausnahme (File={FileId}) — Worker läuft weiter", current?.FileId);
            }
        }
    }

    private async Task ProcessJobAsync(ThumbnailRequest req)
    {
        var cachePath = BuildCachePath(req.LibraryId, req.FileId);
        try
        {
            if (!File.Exists(req.SourcePath))
            {
                // Quelle fehlt (Offline-Root, gelöscht): kein Renderer, kein Versuch verbraucht.
                var missing = ThumbnailRenderResult.Fail(ThumbnailErrorCodes.SourceMissing, $"Quelldatei fehlt: {req.SourcePath}");
                await UpsertStatusAsync(req, ThumbnailStatus.Failed, cachePath, missing, attemptDelta: 0).ConfigureAwait(false);
                SetState(req, JobOutcome.Failed, retryable: true);
                return;
            }

            await UpsertStatusAsync(req, ThumbnailStatus.Pending, cachePath, null, attemptDelta: +1).ConfigureAwait(false);

            ThumbnailRenderResult result = req.MediaType == MediaType.Video
                ? await _videoRenderer.RenderAsync(req.SourcePath, cachePath, Size, _cts.Token).ConfigureAwait(false)
                : await RenderImageWithFallbackAsync(req, cachePath).ConfigureAwait(false);

            if (result.Success)
            {
                // Das Thumbnail existiert — ein Fehler beim Status-Schreiben darf es nicht verstecken
                // (die Zeile bleibt Pending → beim nächsten Start Stale → erneut gerendert).
                try
                {
                    await UpsertStatusAsync(req, ThumbnailStatus.Generated, cachePath, result, attemptDelta: 0).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Thumb erzeugt, aber DB-Status konnte nicht geschrieben werden: File={FileId}", req.FileId);
                }

                SetState(req, JobOutcome.Done);
                RaiseReady(new ThumbnailReadyEventArgs(req.LibraryId, req.FileId, cachePath));
            }
            else
            {
                var retryable = IsRetryable(result.ErrorCode);
                await UpsertStatusAsync(req, ThumbnailStatus.Failed, cachePath, result, attemptDelta: retryable ? -1 : 0).ConfigureAwait(false);
                SetState(req, JobOutcome.Failed, retryable);
                _logger.LogDebug(
                    "Thumb fehlgeschlagen: File={FileId} {Code}: {Msg}",
                    req.FileId, result.ErrorCode, result.ErrorMessage);
            }
        }
        catch (OperationCanceledException)
        {
            _states.TryRemove(req.FileId, out _);
        }
        catch (Exception ex)
        {
            SetState(req, JobOutcome.Failed);
            _logger.LogWarning(ex, "Thumb-Job-Ausnahme: File={FileId}", req.FileId);
        }
        finally
        {
            Interlocked.Decrement(ref _pending);
        }
    }

    /// <summary>Fehlschläge mit äußerer Ursache: die Datei selbst ist nicht „kaputt“, ein späterer Versuch kann gelingen.</summary>
    private static bool IsRetryable(string? errorCode)
        => errorCode is ThumbnailErrorCodes.SourceMissing or ThumbnailErrorCodes.FfmpegNotFound;

    /// <summary>
    /// Bild via WIC; scheitert WIC am Decoding (<see cref="ThumbnailErrorCodes.WicDecodeFailed"/>)
    /// und ist der Fallback-Renderer verfügbar, zweiter Versuch via FFmpeg. Erst danach Failed —
    /// mit dem ErrorCode des Fallbacks und beiden Fehlertexten.
    /// </summary>
    private async Task<ThumbnailRenderResult> RenderImageWithFallbackAsync(ThumbnailRequest req, string cachePath)
    {
        var result = RenderImageLowPriority(req.SourcePath, cachePath);
        if (result.Success
            || result.ErrorCode != ThumbnailErrorCodes.WicDecodeFailed
            || _fallbackRenderer is not { IsAvailable: true })
        {
            return result;
        }

        _logger.LogDebug("WIC konnte {Path} nicht decodieren — FFmpeg-Fallback", req.SourcePath);
        var fallback = await _fallbackRenderer.RenderAsync(req.SourcePath, cachePath, Size, _cts.Token).ConfigureAwait(false);
        if (fallback.Success)
        {
            return fallback;
        }

        return ThumbnailRenderResult.Fail(
            fallback.ErrorCode ?? ThumbnailErrorCodes.FfmpegFailed,
            $"WIC: {result.ErrorMessage} | FFmpeg: {fallback.ErrorMessage}");
    }

    /// <summary>Abonnenten laufen auf dem Worker-Thread; ihre Ausnahmen dürfen weder Job-Zustand noch Worker beschädigen.</summary>
    private void RaiseReady(ThumbnailReadyEventArgs args)
    {
        try
        {
            ThumbnailReady?.Invoke(args);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ThumbnailReady-Abonnent warf eine Ausnahme (File={FileId})", args.FileId);
        }
    }

    /// <param name="attemptDelta">+1 beim Start eines Versuchs (Pending), −1 wenn ein Fehlschlag mit äußerer Ursache den Versuch nicht zählen soll.</param>
    private async Task UpsertStatusAsync(
        ThumbnailRequest req,
        ThumbnailStatus status,
        string cachePath,
        ThumbnailRenderResult? result,
        int attemptDelta)
    {
        await _dbWriteLock.WaitAsync(_cts.Token).ConfigureAwait(false);
        try
        {
            await using var ctx = _factory.Create();
            var entity = await ctx.Thumbnails
                .FindAsync(new object[] { req.FileId, Size, ThumbnailKind.Still }, _cts.Token)
                .ConfigureAwait(false);

            if (entity is null)
            {
                entity = new Thumbnail
                {
                    FileId = req.FileId,
                    Size = Size,
                    Kind = ThumbnailKind.Still,
                    Format = "jpg",
                    CachePath = cachePath,
                    SourceModifiedAtUtc = req.SourceModifiedAtUtc,
                    Status = status,
                };
                ctx.Thumbnails.Add(entity);
            }
            else
            {
                entity.CachePath = cachePath;
                entity.SourceModifiedAtUtc = req.SourceModifiedAtUtc;
                entity.Status = status;
            }

            entity.AttemptCount = Math.Max(0, entity.AttemptCount + attemptDelta);

            switch (status)
            {
                case ThumbnailStatus.Pending:
                    entity.LastAttemptAtUtc = DateTime.UtcNow;
                    break;
                case ThumbnailStatus.Generated when result is not null:
                    entity.GeneratedAtUtc = DateTime.UtcNow;
                    entity.ByteSize = result.ByteSize;
                    entity.ErrorCode = null;
                    entity.ErrorMessage = null;
                    break;
                case ThumbnailStatus.Failed when result is not null:
                    entity.LastAttemptAtUtc ??= DateTime.UtcNow;
                    entity.ErrorCode = result.ErrorCode;
                    entity.ErrorMessage = Truncate(result.ErrorMessage, ErrorMessageMaxLen);
                    break;
            }

            await ctx.SaveChangesAsync(_cts.Token).ConfigureAwait(false);
        }
        finally
        {
            _dbWriteLock.Release();
        }
    }

    /// <summary>Entfernt alte <c>.tmp</c>-Dateien unter <c>thumbs\{libraryId}\</c> — Reste abgestürzter Renderer-Läufe.</summary>
    private void CleanupTempFiles()
    {
        try
        {
            var root = _paths.ThumbnailsRoot;
            if (!Directory.Exists(root))
            {
                return;
            }

            var cutoff = DateTime.UtcNow - TempFileMaxAge;
            var removed = 0;
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                foreach (var tmp in Directory.EnumerateFiles(dir, "*.tmp"))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(tmp) <= cutoff)
                        {
                            File.Delete(tmp);
                            removed++;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        _logger.LogDebug(ex, "Thumbnail-.tmp konnte nicht entfernt werden: {Path}", tmp);
                    }
                }
            }

            if (removed > 0)
            {
                _logger.LogInformation("Thumbnail-Cache: {Count} verwaiste .tmp-Dateien entfernt", removed);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Thumbnail-.tmp-Bereinigung übersprungen");
        }
    }

    /// <summary>WIC-Decode ist CPU-lastig: während des Renderns läuft der Thread mit niedriger Priorität, damit UI und Eingabe Vorrang haben.</summary>
    private ThumbnailRenderResult RenderImageLowPriority(string sourcePath, string cachePath)
    {
        var thread = Thread.CurrentThread;
        var previous = thread.Priority;
        try
        {
            thread.Priority = ThreadPriority.BelowNormal;
            return _imageRenderer.Render(sourcePath, cachePath, Size);
        }
        finally
        {
            thread.Priority = previous;
        }
    }

    private string BuildCachePath(int libraryId, int fileId)
        => Path.Combine(_paths.GetLibraryThumbnailsPath(libraryId), $"{fileId}_{Size}_still.jpg");

    private static string? Truncate(string? value, int maxLen)
        => value is not null && value.Length > maxLen ? value[..maxLen] : value;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _cts.Cancel();
        _imageChannel.Writer.TryComplete();
        _videoChannel.Writer.TryComplete();
        try
        {
            Task.WaitAll(_workers.ToArray(), TimeSpan.FromSeconds(3));
        }
        catch
        {
            // Worker wurden gecancelt — AggregateException erwartet
        }

        _cts.Dispose();
        _dbWriteLock.Dispose();
    }
}
