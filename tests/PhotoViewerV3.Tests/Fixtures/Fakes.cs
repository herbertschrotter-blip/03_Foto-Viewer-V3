using CommunityToolkit.Mvvm.Messaging;
using PhotoViewerV3.Services.Messaging;
using PhotoViewerV3.Services.Operations;
using PhotoViewerV3.Services.Thumbnails;

namespace PhotoViewerV3.Tests.Fixtures;

/// <summary>Papierkorb-Fake: merkt sich Aufrufe und löscht die Datei (simuliert „Datei ist weg“).</summary>
public sealed class FakeRecycleBin : IRecycleBin
{
    private readonly object _gate = new();
    private readonly List<string> _recycled = new();

    public bool DeleteFiles { get; set; } = true;

    public Exception? ThrowOnRecycle { get; set; }

    public IReadOnlyList<string> Recycled
    {
        get
        {
            lock (_gate)
            {
                return _recycled.ToList();
            }
        }
    }

    public void MoveToRecycleBin(string filePath)
    {
        if (ThrowOnRecycle is not null)
        {
            throw ThrowOnRecycle;
        }

        lock (_gate)
        {
            _recycled.Add(filePath);
        }

        if (DeleteFiles)
        {
            File.Delete(filePath);
        }
    }
}

/// <summary>Bild-Renderer-Fake: schreibt eine Dummy-Datei oder scheitert mit konfigurierbarem ErrorCode.</summary>
public sealed class FakeImageRenderer : IImageThumbnailRenderer
{
    private int _calls;

    public string? FailCode { get; set; }

    public int Calls => Volatile.Read(ref _calls);

    public ThumbnailRenderResult Render(string sourcePath, string targetThumbPath, int targetWidth)
    {
        Interlocked.Increment(ref _calls);
        if (FailCode is not null)
        {
            return ThumbnailRenderResult.Fail(FailCode, "Fake-Bildrenderer scheitert absichtlich");
        }

        return FakeRenderHelper.WriteThumb(targetThumbPath, targetWidth);
    }
}

/// <summary>Video-Renderer-Fake (FFmpeg-Ersatz).</summary>
public sealed class FakeVideoRenderer : IVideoThumbnailRenderer
{
    private int _calls;

    public bool IsAvailable { get; set; } = true;

    public string? FailCode { get; set; }

    public int Calls => Volatile.Read(ref _calls);

    public Task<ThumbnailRenderResult> RenderAsync(string sourcePath, string targetThumbPath, int targetWidth, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        if (FailCode is not null)
        {
            return Task.FromResult(ThumbnailRenderResult.Fail(FailCode, "Fake-Videorenderer scheitert absichtlich"));
        }

        return Task.FromResult(FakeRenderHelper.WriteThumb(targetThumbPath, targetWidth));
    }
}

/// <summary>Fallback-Renderer-Fake (FFmpeg-Bildfallback).</summary>
public sealed class FakeFallbackRenderer : IFallbackImageThumbnailRenderer
{
    private int _calls;

    public bool IsAvailable { get; set; } = true;

    public string? FailCode { get; set; }

    public int Calls => Volatile.Read(ref _calls);

    public Task<ThumbnailRenderResult> RenderAsync(string sourcePath, string targetThumbPath, int targetWidth, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        if (FailCode is not null)
        {
            return Task.FromResult(ThumbnailRenderResult.Fail(FailCode, "Fake-Fallback scheitert absichtlich"));
        }

        return Task.FromResult(FakeRenderHelper.WriteThumb(targetThumbPath, targetWidth));
    }
}

internal static class FakeRenderHelper
{
    public static ThumbnailRenderResult WriteThumb(string targetThumbPath, int targetWidth)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetThumbPath)!);
        var tmp = targetThumbPath + ".tmp";
        File.WriteAllBytes(tmp, new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });
        File.Move(tmp, targetThumbPath, overwrite: true);
        return ThumbnailRenderResult.Ok(targetWidth, targetWidth * 3 / 4, new FileInfo(targetThumbPath).Length);
    }
}

/// <summary>Sammelt <see cref="FilesChangedMessage"/>s eines Messengers (thread-sicher).</summary>
public sealed class MessageCollector
{
    private readonly object _gate = new();
    private readonly List<FilesChangedMessage> _messages = new();

    public MessageCollector(IMessenger messenger)
    {
        messenger.Register<MessageCollector, FilesChangedMessage>(this, static (r, m) => r.Add(m));
    }

    public IReadOnlyList<FilesChangedMessage> FilesChanged
    {
        get
        {
            lock (_gate)
            {
                return _messages.ToList();
            }
        }
    }

    public IReadOnlyList<FilesChangedMessage> OfKind(FilesChangeKind kind)
        => FilesChanged.Where(m => m.Kind == kind).ToList();

    public void Clear()
    {
        lock (_gate)
        {
            _messages.Clear();
        }
    }

    private void Add(FilesChangedMessage message)
    {
        lock (_gate)
        {
            _messages.Add(message);
        }
    }
}
