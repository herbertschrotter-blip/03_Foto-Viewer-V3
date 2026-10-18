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
/// </summary>
public sealed class ThumbnailService : IThumbnailService, IDisposable
{
    public const int Size = 320;

    /// <summary>Nach so vielen gescheiterten Versuchen wird eine Datei nicht mehr eingereiht.</summary>
    public const int MaxAttempts = 3;

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

    private readonly ConcurrentDictionary<int, JobOutcome> _states = new();
    private readonly SemaphoreSlim _dbWriteLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _workers = new();
    private int _started;
    private int _pending;

    private enum JobOutcome { Queued, Done, Failed }

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

    public async Task RequestThumbnailsAsync(
        IReadOnlyList<ThumbnailRequest> requests,
        CancellationToken cancellationToken = default)
    {
        if (requests.Count == 0)
        {
            return;
        }

        EnsureStarted();

        // 1) Schon bekannte Files direkt behandeln, Rest sammeln.
        var toCheck = new List<ThumbnailRequest>(requests.Count);
        foreach (var req in requests)
        {
            if (_states.TryGetValue(req.FileId, out var outcome))
            {
                if (outcome == JobOutcome.Done)
                {
                    var path = BuildCachePath(req.LibraryId, req.FileId);
                    if (File.Exists(path))
                    {
                        ThumbnailReady?.Invoke(new ThumbnailReadyEventArgs(req.LibraryId, req.FileId, path));
                    }
                }

                // Queued → in Arbeit, Failed → nicht erneut versuchen (diese Session)
                continue;
            }

            toCheck.Add(req);
        }

        if (toCheck.Count == 0)
        {
            return;
        }

        // 2) Batch-DB-Check: existierende Thumbnail-Einträge für diese FileIds (ein Query).
        var ids = toCheck.Select(r => r.FileId).ToList();
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

        // 3) Pro Request entscheiden: cache-hit, skip (zu oft gescheitert) oder enqueue.
        foreach (var req in toCheck)
        {
            existing.TryGetValue(req.FileId, out var row);

            var cacheHit = row is { Status: ThumbnailStatus.Generated }
                && row.SourceModifiedAtUtc == req.SourceModifiedAtUtc
                && File.Exists(row.CachePath);
            if (cacheHit)
            {
                _states[req.FileId] = JobOutcome.Done;
                ThumbnailReady?.Invoke(new ThumbnailReadyEventArgs(req.LibraryId, req.FileId, row!.CachePath));
                continue;
            }

            if (row is { Status: ThumbnailStatus.Failed } && row.AttemptCount >= MaxAttempts)
            {
                _states[req.FileId] = JobOutcome.Failed;
                continue;
            }

            if (_states.TryAdd(req.FileId, JobOutcome.Queued))
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

    private void EnsureStarted()
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
        {
            return;
        }

        var imageWorkers = Math.Max(1, Environment.ProcessorCount - 2);
        for (var i = 0; i < imageWorkers; i++)
        {
            _workers.Add(Task.Run(() => WorkerLoopAsync(_imageChannel.Reader)));
        }

        // Video: max. 1 Worker (FFmpeg ist teuer, ADR-0007).
        _workers.Add(Task.Run(() => WorkerLoopAsync(_videoChannel.Reader)));

        _logger.LogInformation(
            "ThumbnailService gestartet: {ImageWorkers} Bild-Worker + 1 Video-Worker (FFmpeg verfügbar: {Ffmpeg})",
            imageWorkers, _videoRenderer.IsAvailable);
    }

    private async Task WorkerLoopAsync(ChannelReader<ThumbnailRequest> reader)
    {
        try
        {
            while (await reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
            {
                while (reader.TryRead(out var req))
                {
                    await ProcessJobAsync(req).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Thumbnail-Worker abgestürzt");
        }
    }

    private async Task ProcessJobAsync(ThumbnailRequest req)
    {
        var cachePath = BuildCachePath(req.LibraryId, req.FileId);
        try
        {
            await UpsertStatusAsync(req, ThumbnailStatus.Pending, cachePath, null).ConfigureAwait(false);

            ThumbnailRenderResult result = req.MediaType == MediaType.Video
                ? await _videoRenderer.RenderAsync(req.SourcePath, cachePath, Size, _cts.Token).ConfigureAwait(false)
                : await RenderImageWithFallbackAsync(req, cachePath).ConfigureAwait(false);

            if (result.Success)
            {
                await UpsertStatusAsync(req, ThumbnailStatus.Generated, cachePath, result).ConfigureAwait(false);
                _states[req.FileId] = JobOutcome.Done;
                ThumbnailReady?.Invoke(new ThumbnailReadyEventArgs(req.LibraryId, req.FileId, cachePath));
            }
            else
            {
                await UpsertStatusAsync(req, ThumbnailStatus.Failed, cachePath, result).ConfigureAwait(false);
                _states[req.FileId] = JobOutcome.Failed;
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
            _states[req.FileId] = JobOutcome.Failed;
            _logger.LogWarning(ex, "Thumb-Job-Ausnahme: File={FileId}", req.FileId);
        }
        finally
        {
            Interlocked.Decrement(ref _pending);
        }
    }

    /// <summary>
    /// Bild via WIC; scheitert WIC am Decoding (<see cref="ThumbnailErrorCodes.WicDecodeFailed"/>)
    /// und ist der Fallback-Renderer verfügbar, zweiter Versuch via FFmpeg. Erst danach Failed —
    /// mit dem ErrorCode des Fallbacks und beiden Fehlertexten.
    /// </summary>
    private async Task<ThumbnailRenderResult> RenderImageWithFallbackAsync(ThumbnailRequest req, string cachePath)
    {
        var result = _imageRenderer.Render(req.SourcePath, cachePath, Size);
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

    private async Task UpsertStatusAsync(
        ThumbnailRequest req,
        ThumbnailStatus status,
        string cachePath,
        ThumbnailRenderResult? result)
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

            switch (status)
            {
                case ThumbnailStatus.Pending:
                    entity.LastAttemptAtUtc = DateTime.UtcNow;
                    entity.AttemptCount += 1;
                    break;
                case ThumbnailStatus.Generated when result is not null:
                    entity.GeneratedAtUtc = DateTime.UtcNow;
                    entity.ByteSize = result.ByteSize;
                    entity.ErrorCode = null;
                    entity.ErrorMessage = null;
                    break;
                case ThumbnailStatus.Failed when result is not null:
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

    private string BuildCachePath(int libraryId, int fileId)
        => Path.Combine(_paths.GetLibraryThumbnailsPath(libraryId), $"{fileId}_{Size}_still.jpg");

    private static string? Truncate(string? value, int maxLen)
        => value is not null && value.Length > maxLen ? value[..maxLen] : value;

    public void Dispose()
    {
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
