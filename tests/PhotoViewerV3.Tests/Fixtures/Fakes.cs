using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Data.Sqlite;
using PhotoViewerV3.Data;
using PhotoViewerV3.Services.Database;
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

    /// <summary>Wird vor dem Löschen aufgerufen — z. B. um mitten im Dateisystem-Schritt abzubrechen oder den DB-Stand zu prüfen.</summary>
    public Action<string>? OnRecycle { get; set; }

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

        OnRecycle?.Invoke(filePath);

        lock (_gate)
        {
            _recycled.Add(filePath);
        }

        if (DeleteFiles)
        {
            File.Delete(filePath);
        }
    }

    /// <summary>Ordner (ADR-0034): merkt sich den Aufruf und löscht den Ordner samt Inhalt (simuliert „im Papierkorb“).</summary>
    public void MoveDirectoryToRecycleBin(string directoryPath)
    {
        if (ThrowOnRecycle is not null)
        {
            throw ThrowOnRecycle;
        }

        OnRecycle?.Invoke(directoryPath);

        lock (_gate)
        {
            _recycled.Add(directoryPath);
        }

        if (DeleteFiles)
        {
            Directory.Delete(directoryPath, recursive: true);
        }
    }
}

/// <summary>Bild-Renderer-Fake: schreibt eine Dummy-Datei oder scheitert mit konfigurierbarem ErrorCode.</summary>
public sealed class FakeImageRenderer : IImageThumbnailRenderer
{
    private int _calls;

    public string? FailCode { get; set; }

    public int Calls => Volatile.Read(ref _calls);

    /// <summary>Maße des Originals nach Drehung, die Render meldet (0 = unbekannt, ADR-0043).</summary>
    public (int Width, int Height) SourceSize { get; set; }

    /// <summary>Antwort von <see cref="ReadDimensions"/> (Nachtragen aus dem Dateikopf); null = nicht lesbar.</summary>
    public (int Width, int Height)? HeaderSize { get; set; }

    public int DimensionReads => Volatile.Read(ref _dimensionReads);

    private int _dimensionReads;

    public ThumbnailRenderResult Render(string sourcePath, string targetThumbPath, int targetWidth)
    {
        Interlocked.Increment(ref _calls);
        if (FailCode is not null)
        {
            return ThumbnailRenderResult.Fail(FailCode, "Fake-Bildrenderer scheitert absichtlich");
        }

        return FakeRenderHelper.WriteThumb(targetThumbPath, targetWidth) with
        {
            SourceWidth = SourceSize.Width,
            SourceHeight = SourceSize.Height,
        };
    }

    public (int Width, int Height)? ReadDimensions(string sourcePath)
    {
        Interlocked.Increment(ref _dimensionReads);
        return HeaderSize;
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

/// <summary>Context-Factory-Dekorator: der n-te <see cref="Create"/>-Aufruf wirft (simulierter DB-Ausfall), alle anderen laufen durch.</summary>
public sealed class FailingContextFactory : ILibraryContextFactory
{
    private readonly ILibraryContextFactory _inner;
    private int _creates;

    public FailingContextFactory(ILibraryContextFactory inner, int failOnCreate)
    {
        _inner = inner;
        FailOnCreate = failOnCreate;
    }

    /// <summary>1-basierte Nummer des Create-Aufrufs, der wirft; 0 = nie.</summary>
    public int FailOnCreate { get; set; }

    public int Creates => Volatile.Read(ref _creates);

    public string DatabasePath => _inner.DatabasePath;

    public string ConnectionString => _inner.ConnectionString;

    public LibraryContext Create()
    {
        var n = Interlocked.Increment(ref _creates);
        if (FailOnCreate > 0 && n == FailOnCreate)
        {
            throw new InvalidOperationException($"Simulierter DB-Ausfall bei Create #{n}");
        }

        return _inner.Create();
    }

    public SqliteConnection OpenSqliteConnection() => _inner.OpenSqliteConnection();
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
