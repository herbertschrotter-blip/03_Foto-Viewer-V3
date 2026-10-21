using Lumora.Services.Database.Relocation;
using Lumora.Services.Thumbnails;
using Lumora.Services.Watch;

namespace Lumora.Tests.Database;

/// <summary>Zählt Pause/Resume; keine Worker.</summary>
public sealed class PausableThumbnailServiceFake : IThumbnailService
{
    public int PauseCalls { get; private set; }

    public int ResumeCalls { get; private set; }

    public bool IsPaused => PauseCalls > ResumeCalls;

    public int ThumbnailSize => 320;

    public int PendingCount => 0;

    public event Action<ThumbnailReadyEventArgs>? ThumbnailReady
    {
        add { }
        remove { }
    }

    public Task RequestThumbnailsAsync(IReadOnlyList<ThumbnailRequest> requests, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task PauseAsync(CancellationToken ct = default)
    {
        PauseCalls++;
        return Task.CompletedTask;
    }

    public void Resume() => ResumeCalls++;
}

/// <summary>Zählt Start/Stop der Ordnerüberwachung.</summary>
public sealed class WatchServiceFake : ILibraryWatchService
{
    public int StartCalls { get; private set; }

    public int StopCalls { get; private set; }

    /// <summary>Wie in der App: läuft anfangs (gestartet beim App-Start).</summary>
    public bool IsRunning => StartCalls >= StopCalls;

    public bool IsEnabled => true;

    public TimeSpan QuietPeriod => TimeSpan.FromSeconds(3);

    public WatchStatus Status => new(null, default);

    public Task StartAsync(CancellationToken ct = default)
    {
        StartCalls++;
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        StopCalls++;
        return Task.CompletedTask;
    }

    public Task SetEnabledAsync(bool enabled, CancellationToken ct = default) => Task.CompletedTask;

    public Task SetQuietPeriodAsync(TimeSpan quietPeriod, CancellationToken ct = default) => Task.CompletedTask;

    public void Dispose()
    {
    }
}

/// <summary>Laufwerks-Eigenschaften zum Einstellen: freier Platz, Netzlaufwerk, OneDrive.</summary>
public sealed class StorageProbeFake : IStorageProbe
{
    public long? AvailableBytes { get; set; } = 100L * 1024 * 1024 * 1024;

    public bool IsNetwork { get; set; }

    public bool IsCloud { get; set; }

    public long? GetAvailableFreeSpace(string folder) => AvailableBytes;

    public bool IsNetworkLocation(string folder) => IsNetwork;

    public bool IsCloudSynced(string folder) => IsCloud;
}
