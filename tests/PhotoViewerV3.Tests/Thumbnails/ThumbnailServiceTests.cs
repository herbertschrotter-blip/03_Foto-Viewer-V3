using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PhotoViewerV3.Domain.Entities;
using PhotoViewerV3.Domain.Enums;
using PhotoViewerV3.Services.Thumbnails;
using PhotoViewerV3.Tests.Fixtures;

namespace PhotoViewerV3.Tests.Thumbnails;

public sealed class ThumbnailServiceTests
{
    private const int LibraryId = 1;
    private static readonly DateTime Modified = new(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private static ThumbnailService CreateService(
        TempLibraryFixture fx,
        FakeImageRenderer image,
        FakeVideoRenderer? video = null,
        FakeFallbackRenderer? fallback = null)
        => new(fx.ContextFactory, fx.Paths, image, video ?? new FakeVideoRenderer(), NullLogger<ThumbnailService>.Instance, fallback);

    private static ThumbnailRequest ImageRequest(TempLibraryFixture fx, int fileId)
        => new(LibraryId, fileId, fx.WriteLibraryFile($"img{fileId}.jpg"), MediaType.Image, Modified);

    private static async Task WaitForIdleAsync(ThumbnailService svc)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (svc.PendingCount > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        svc.PendingCount.Should().Be(0);
    }

    private static async Task<Thumbnail> GetRowAsync(TempLibraryFixture fx, int fileId)
    {
        await using var ctx = fx.CreateContext();
        return await ctx.Thumbnails.AsNoTracking().SingleAsync(t => t.FileId == fileId && t.Size == ThumbnailService.Size);
    }

    private static async Task SeedRowAsync(TempLibraryFixture fx, int fileId, ThumbnailStatus status, int attempts, string cachePath, string? errorCode = null)
    {
        await using var ctx = fx.CreateContext();
        ctx.Thumbnails.Add(new Thumbnail
        {
            FileId = fileId,
            Size = ThumbnailService.Size,
            Kind = ThumbnailKind.Still,
            Format = "jpg",
            CachePath = cachePath,
            SourceModifiedAtUtc = Modified,
            GeneratedAtUtc = status == ThumbnailStatus.Generated ? Modified : null,
            ByteSize = 4,
            Status = status,
            AttemptCount = attempts,
            ErrorCode = errorCode,
        });
        await ctx.SaveChangesAsync();
    }

    [Fact]
    public async Task CacheHit_meldet_sofort_ThumbnailReady_ohne_Renderer()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var cachePath = Path.Combine(fx.Paths.GetLibraryThumbnailsPath(LibraryId), $"5_{ThumbnailService.Size}_still.jpg");
        TempLibraryFixture.WriteFile(cachePath, 100);
        await SeedRowAsync(fx, 5, ThumbnailStatus.Generated, 1, cachePath);
        var image = new FakeImageRenderer();
        using var svc = CreateService(fx, image);
        var ready = new List<ThumbnailReadyEventArgs>();
        svc.ThumbnailReady += e => { lock (ready) { ready.Add(e); } };

        await svc.RequestThumbnailsAsync(new[] { ImageRequest(fx, 5) });

        ready.Should().ContainSingle();
        ready[0].FileId.Should().Be(5);
        ready[0].LibraryId.Should().Be(LibraryId);
        ready[0].ThumbnailPath.Should().Be(cachePath);
        image.Calls.Should().Be(0);
        svc.PendingCount.Should().Be(0);

        // Zweite Anfrage: In-Memory-State liefert erneut sofort, ohne DB-Roundtrip-Fehler.
        await svc.RequestThumbnailsAsync(new[] { ImageRequest(fx, 5) });
        ready.Should().HaveCount(2);
    }

    [Fact]
    public async Task Bild_wird_generiert_Status_Pending_zu_Generated()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var image = new FakeImageRenderer();
        using var svc = CreateService(fx, image);
        var ready = new TaskCompletionSource<ThumbnailReadyEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        svc.ThumbnailReady += e => ready.TrySetResult(e);

        await svc.RequestThumbnailsAsync(new[] { ImageRequest(fx, 42) });
        var args = await ready.Task.WaitAsync(Timeout);
        await WaitForIdleAsync(svc);

        args.FileId.Should().Be(42);
        args.ThumbnailPath.Should().Be(Path.Combine(fx.Paths.GetLibraryThumbnailsPath(LibraryId), $"42_{ThumbnailService.Size}_still.jpg"));
        File.Exists(args.ThumbnailPath).Should().BeTrue();
        image.Calls.Should().Be(1);

        var row = await GetRowAsync(fx, 42);
        row.Status.Should().Be(ThumbnailStatus.Generated);
        row.AttemptCount.Should().Be(1);
        row.ByteSize.Should().BeGreaterThan(0);
        row.GeneratedAtUtc.Should().NotBeNull();
        row.LastAttemptAtUtc.Should().NotBeNull();
        row.SourceModifiedAtUtc.Should().Be(Modified);
        row.CachePath.Should().Be(args.ThumbnailPath);
        row.ErrorCode.Should().BeNull();
    }

    [Fact]
    public async Task Fallback_greift_bei_wic_decode_failed()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var image = new FakeImageRenderer { FailCode = ThumbnailErrorCodes.WicDecodeFailed };
        var fallback = new FakeFallbackRenderer();
        using var svc = CreateService(fx, image, fallback: fallback);
        var ready = new TaskCompletionSource<ThumbnailReadyEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        svc.ThumbnailReady += e => ready.TrySetResult(e);

        await svc.RequestThumbnailsAsync(new[] { ImageRequest(fx, 7) });
        var args = await ready.Task.WaitAsync(Timeout);
        await WaitForIdleAsync(svc);

        args.FileId.Should().Be(7);
        File.Exists(args.ThumbnailPath).Should().BeTrue();
        image.Calls.Should().Be(1);
        fallback.Calls.Should().Be(1);
        var row = await GetRowAsync(fx, 7);
        row.Status.Should().Be(ThumbnailStatus.Generated);
        row.ErrorCode.Should().BeNull();
    }

    [Fact]
    public async Task Fallback_wird_bei_anderen_Fehlern_nicht_gerufen()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var image = new FakeImageRenderer { FailCode = ThumbnailErrorCodes.SourceMissing };
        var fallback = new FakeFallbackRenderer();
        using var svc = CreateService(fx, image, fallback: fallback);

        await svc.RequestThumbnailsAsync(new[] { ImageRequest(fx, 8) });
        await WaitForIdleAsync(svc);

        fallback.Calls.Should().Be(0);
        var row = await GetRowAsync(fx, 8);
        row.Status.Should().Be(ThumbnailStatus.Failed);
        row.ErrorCode.Should().Be(ThumbnailErrorCodes.SourceMissing);
    }

    [Fact]
    public async Task Ohne_verfuegbaren_Fallback_bleibt_wic_decode_failed()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var image = new FakeImageRenderer { FailCode = ThumbnailErrorCodes.WicDecodeFailed };
        var unavailable = new FakeFallbackRenderer { IsAvailable = false };
        using var svcWithUnavailable = CreateService(fx, image, fallback: unavailable);
        using var svcWithout = CreateService(fx, image);
        var fired = 0;
        svcWithUnavailable.ThumbnailReady += _ => Interlocked.Increment(ref fired);
        svcWithout.ThumbnailReady += _ => Interlocked.Increment(ref fired);

        await svcWithUnavailable.RequestThumbnailsAsync(new[] { ImageRequest(fx, 9) });
        await svcWithout.RequestThumbnailsAsync(new[] { ImageRequest(fx, 10) });
        await WaitForIdleAsync(svcWithUnavailable);
        await WaitForIdleAsync(svcWithout);

        unavailable.Calls.Should().Be(0);
        fired.Should().Be(0);
        (await GetRowAsync(fx, 9)).ErrorCode.Should().Be(ThumbnailErrorCodes.WicDecodeFailed);
        (await GetRowAsync(fx, 10)).ErrorCode.Should().Be(ThumbnailErrorCodes.WicDecodeFailed);
        (await GetRowAsync(fx, 9)).Status.Should().Be(ThumbnailStatus.Failed);
    }

    [Fact]
    public async Task Fallback_Fehler_liefert_Failed_mit_FFmpeg_Code_und_beiden_Fehlertexten()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var image = new FakeImageRenderer { FailCode = ThumbnailErrorCodes.WicDecodeFailed };
        var fallback = new FakeFallbackRenderer { FailCode = ThumbnailErrorCodes.FfmpegFailed };
        using var svc = CreateService(fx, image, fallback: fallback);

        await svc.RequestThumbnailsAsync(new[] { ImageRequest(fx, 11) });
        await WaitForIdleAsync(svc);

        fallback.Calls.Should().Be(1);
        var row = await GetRowAsync(fx, 11);
        row.Status.Should().Be(ThumbnailStatus.Failed);
        row.ErrorCode.Should().Be(ThumbnailErrorCodes.FfmpegFailed);
        row.ErrorMessage.Should().Contain("WIC:").And.Contain("FFmpeg:");
        row.AttemptCount.Should().Be(1);
    }

    [Fact]
    public async Task MaxAttempts_verhindert_erneute_Versuche()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var cachePath = Path.Combine(fx.Paths.GetLibraryThumbnailsPath(LibraryId), $"12_{ThumbnailService.Size}_still.jpg");
        await SeedRowAsync(fx, 12, ThumbnailStatus.Failed, ThumbnailService.MaxAttempts, cachePath, ThumbnailErrorCodes.WicDecodeFailed);
        var image = new FakeImageRenderer();
        using var svc = CreateService(fx, image);
        var fired = 0;
        svc.ThumbnailReady += _ => Interlocked.Increment(ref fired);

        await svc.RequestThumbnailsAsync(new[] { ImageRequest(fx, 12) });
        await WaitForIdleAsync(svc);

        image.Calls.Should().Be(0);
        fired.Should().Be(0);
        (await GetRowAsync(fx, 12)).AttemptCount.Should().Be(ThumbnailService.MaxAttempts);
    }

    [Fact]
    public async Task Fehlgeschlagene_Datei_unter_MaxAttempts_wird_erneut_versucht_und_Versuche_gezaehlt()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var cachePath = Path.Combine(fx.Paths.GetLibraryThumbnailsPath(LibraryId), $"13_{ThumbnailService.Size}_still.jpg");
        await SeedRowAsync(fx, 13, ThumbnailStatus.Failed, ThumbnailService.MaxAttempts - 1, cachePath, ThumbnailErrorCodes.WicDecodeFailed);
        var image = new FakeImageRenderer();
        using var svc = CreateService(fx, image);
        var ready = new TaskCompletionSource<ThumbnailReadyEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        svc.ThumbnailReady += e => ready.TrySetResult(e);

        await svc.RequestThumbnailsAsync(new[] { ImageRequest(fx, 13) });
        await ready.Task.WaitAsync(Timeout);
        await WaitForIdleAsync(svc);

        image.Calls.Should().Be(1);
        var row = await GetRowAsync(fx, 13);
        row.Status.Should().Be(ThumbnailStatus.Generated);
        row.AttemptCount.Should().Be(ThumbnailService.MaxAttempts);
        row.ErrorCode.Should().BeNull();
    }

    [Fact]
    public async Task PendingCount_endet_bei_0_nach_gemischtem_Batch_und_Video_nutzt_Video_Renderer()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var image = new FakeImageRenderer();
        var video = new FakeVideoRenderer();
        using var svc = CreateService(fx, image, video);
        var readyIds = new List<int>();
        svc.ThumbnailReady += e => { lock (readyIds) { readyIds.Add(e.FileId); } };

        var requests = Enumerable.Range(100, 5).Select(id => ImageRequest(fx, id)).ToList();
        requests.Add(new ThumbnailRequest(LibraryId, 200, fx.WriteLibraryFile("clip.mp4"), MediaType.Video, Modified));
        requests.Add(ImageRequest(fx, 100)); // Duplikat → dedupliziert

        await svc.RequestThumbnailsAsync(requests);
        await WaitForIdleAsync(svc);

        lock (readyIds)
        {
            readyIds.Should().BeEquivalentTo(new[] { 100, 101, 102, 103, 104, 200 });
        }

        image.Calls.Should().Be(5);
        video.Calls.Should().Be(1);
        svc.PendingCount.Should().Be(0);
        await using var ctx = fx.CreateContext();
        (await ctx.Thumbnails.CountAsync(t => t.Status == ThumbnailStatus.Generated)).Should().Be(6);
        File.Exists(Path.Combine(fx.Paths.GetLibraryThumbnailsPath(LibraryId), $"200_{ThumbnailService.Size}_still.jpg")).Should().BeTrue();
    }
}
