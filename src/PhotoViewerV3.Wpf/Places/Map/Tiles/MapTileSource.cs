using System;
using System.Threading;
using System.Threading.Tasks;

namespace PhotoViewerV3.Places.Map.Tiles;

/// <summary>Kachel im OSM-Schema (z/x/y, y nach Süden).</summary>
public readonly record struct MapTileId(int Zoom, int X, int Y)
{
    public override string ToString() => $"{Zoom}/{X}/{Y}";

    /// <summary>Übergeordnete Kachel (eine Stufe gröber) oder null auf Stufe 0.</summary>
    public MapTileId? Parent => Zoom == 0 ? null : new MapTileId(Zoom - 1, X >> 1, Y >> 1);
}

/// <summary>Zustand der Kachelquelle für die Anzeige („offline“, „abgeschaltet“).</summary>
public enum MapTileSourceState
{
    /// <summary>Kacheln werden geladen bzw. kommen aus dem Cache.</summary>
    Online = 0,

    /// <summary>Kein Netz bzw. Server nicht erreichbar — nur Kacheln aus dem Cache, neuer Versuch später.</summary>
    Offline = 1,

    /// <summary>Kachelquelle abgeschaltet (Tests, Rauchtest, PHOTOVIEWERV3_MAP_TILES=off) — graue Karte.</summary>
    Disabled = 2,
}

/// <summary>Liefert Kartenkacheln als PNG/JPEG-Bytes (Netz oder Platten-Cache).</summary>
public interface IMapTileSource
{
    MapTileSourceState State { get; }

    /// <summary>Zuordnung, die sichtbar auf der Karte stehen muss.</summary>
    string Attribution { get; }

    /// <summary>Höchste Zoomstufe der Quelle.</summary>
    int MaxZoom { get; }

    /// <summary>Wird ausgelöst, wenn sich <see cref="State"/> ändert (beliebiger Thread).</summary>
    event EventHandler? StateChanged;

    /// <summary>Kachel oder null (abgeschaltet, offline ohne Cache, Fehler).</summary>
    Task<byte[]?> GetTileAsync(MapTileId id, CancellationToken ct);
}

/// <summary>Abgeschaltete Quelle: liefert nie eine Kachel und fasst das Netz nicht an (Tests, Rauchtest).</summary>
public sealed class DisabledMapTileSource : IMapTileSource
{
    public MapTileSourceState State => MapTileSourceState.Disabled;

    public string Attribution => OsmTileSource.OsmAttribution;

    public int MaxZoom => 19;

    public event EventHandler? StateChanged
    {
        add { }
        remove { }
    }

    public Task<byte[]?> GetTileAsync(MapTileId id, CancellationToken ct) => Task.FromResult<byte[]?>(null);
}

/// <summary>Einstellungen der Kachelquelle (ADR-0051).</summary>
public sealed record MapTileOptions
{
    /// <summary>Umgebungsvariable: „off“ schaltet die Kachelquelle ab (graue Karte, kein Netzzugriff).</summary>
    public const string EnvironmentVariable = "PHOTOVIEWERV3_MAP_TILES";

    /// <summary>false = keine Kacheln laden (Tests, Rauchtest).</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Kachel-URL; {z}/{x}/{y} werden ersetzt.</summary>
    public string UrlTemplate { get; init; } = "https://tile.openstreetmap.org/{z}/{x}/{y}.png";

    /// <summary>Höchstens so viele gleichzeitige Downloads (OSM-Nutzungsregeln: max. 2).</summary>
    public int MaxParallelDownloads { get; init; } = 2;

    /// <summary>Größe des Platten-Caches; darüber werden die am längsten nicht benutzten Kacheln gelöscht (LRU).</summary>
    public long DiskCacheBytes { get; init; } = 500L * 1024 * 1024;

    /// <summary>Mindesthaltbarkeit einer Kachel ohne Cache-Kopfzeilen (OSM: mindestens 7 Tage).</summary>
    public TimeSpan MinimumFreshness { get; init; } = TimeSpan.FromDays(7);

    /// <summary>Nach einem Netzfehler so lange nur aus dem Cache liefern.</summary>
    public TimeSpan OfflineRetry { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Standard; mit PHOTOVIEWERV3_MAP_TILES=off bzw. im Rauchtest abgeschaltet.</summary>
    public static MapTileOptions FromEnvironment()
    {
        var value = Environment.GetEnvironmentVariable(EnvironmentVariable);
        var off = string.Equals(value, "off", StringComparison.OrdinalIgnoreCase) || value == "0"
                  || PhotoViewerV3.Shell.SmokeTestRunner.IsRequested;
        return new MapTileOptions { Enabled = !off };
    }
}
