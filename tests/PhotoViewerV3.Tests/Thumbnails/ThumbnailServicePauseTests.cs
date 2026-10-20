using Microsoft.Extensions.Logging.Abstractions;
using PhotoViewerV3.Domain.Enums;
using PhotoViewerV3.Services.Thumbnails;
using PhotoViewerV3.Tests.Fixtures;

namespace PhotoViewerV3.Tests.Thumbnails;

/// <summary>Pause der Thumbnail-Worker für den Umzug der Katalog-DB (ADR-0026).</summary>
public sealed class ThumbnailServicePauseTests
{
    private static readonly DateTime Modified = new(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Hält jeden Render-Aufruf an, bis <see cref="Release"/> gesetzt ist.</summary>
    private sealed class BlockingImageRenderer : IImageThumbnailRenderer
    {
        private int _calls;

        public ManualResetEventSlim Entered { get; } = new();

        public ManualResetEventSlim Release { get; } = new();

        public int Calls => Volatile.Read(ref _calls);

        public ThumbnailRenderResult Render(string sourcePath, string targetThumbPath, int targetWidth)
        {
            Interlocked.Increment(ref _calls);
            Entered.Set();
            Release.Wait(TimeSpan.FromSeconds(15));
            return FakeRenderHelper.WriteThumb(targetThumbPath, targetWidth);
        }
    }

    [Fact]
    public async Task Pause_wartet_auf_den_laufenden_Auftrag_und_nimmt_bis_Resume_nichts_Neues_an()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var renderer = new BlockingImageRenderer();
        using var svc = new ThumbnailService(fx.ContextFactory, fx.Paths, renderer, new FakeVideoRenderer(), NullLogger<ThumbnailService>.Instance);
        ThumbnailRequest Request(int id) => new(1, id, fx.WriteLibraryFile($"img{id}.jpg"), MediaType.Image, Modified);

        await svc.RequestThumbnailsAsync([Request(1)]);
        renderer.Entered.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();

        var pause = svc.PauseAsync();
        await Task.Delay(100);
        pause.IsCompleted.Should().BeFalse("der laufende Auftrag schreibt noch in die DB");

        renderer.Release.Set();
        await pause.WaitAsync(TimeSpan.FromSeconds(10));
        svc.PendingCount.Should().Be(0);

        await svc.RequestThumbnailsAsync([Request(2)]);
        await Task.Delay(100);
        renderer.Calls.Should().Be(1, "pausiert: keine DB-Zugriffe, Anforderungen werden verworfen");

        svc.Resume();
        await svc.RequestThumbnailsAsync([Request(2)]);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (renderer.Calls < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        renderer.Calls.Should().Be(2);
    }
}
