using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumora.Places.Map.Tiles;

/// <summary>
/// Dekodierte Kartenkacheln für die Karte (ADR-0051): Speicher-Cache (LRU, eingefrorene Bitmaps) vor der
/// <see cref="IMapTileSource"/>. Die Karte meldet nach jeder Bewegung die sichtbaren Kacheln
/// (<see cref="SetWanted"/>); noch nicht begonnene Aufträge für nicht mehr sichtbare Kacheln fallen dabei weg — so lädt
/// die App nie mehr als den aktuellen Ausschnitt (keine Massenabrufe), höchstens zwei Kacheln gleichzeitig.
/// Fertige Kacheln werden gebündelt mit Background-Priorität an den UI-Thread gemeldet (<see cref="TilesChanged"/>).
/// </summary>
public sealed class MapTileProvider
{
    private const int MemoryCapacity = 384;

    /// <summary>Eine Kachel, die nicht kam (offline, Fehler), wird frühestens danach wieder angefragt.</summary>
    private static readonly TimeSpan FailedRetry = TimeSpan.FromSeconds(30);

    private readonly IMapTileSource _source;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly Dictionary<MapTileId, LinkedListNode<(MapTileId Id, ImageSource Image)>> _memory = new();
    private readonly LinkedList<(MapTileId Id, ImageSource Image)> _lru = new();
    private readonly Dictionary<MapTileId, DateTime> _failed = new(); // Kachel → Zeitpunkt des Fehlschlags (30 s Pause)
    private readonly HashSet<MapTileId> _running = new();
    private List<MapTileId> _queue = new();
    private readonly int _parallel;
    private int _flushScheduled;
    private int _generation; // steigt bei jedem Stilwechsel; Kacheln älterer Generationen werden verworfen
    private int _inFlight; // laufende Abrufe aller Generationen: die Grenze von zwei gilt auch über einen Stilwechsel
    private IReadOnlyList<MapTileId> _lastWanted = [];

    public MapTileProvider(IMapTileSource source, MapTileOptions options, ILogger<MapTileProvider>? logger = null)
    {
        _source = source;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _parallel = Math.Clamp(options.MaxParallelDownloads, 1, 2);
        _source.StateChanged += (_, _) => PostToUi(() => StateChanged?.Invoke(this, EventArgs.Empty));
        if (_source is ISwitchableMapTileSource switchable)
        {
            switchable.SourceChanged += (_, _) => Reset(); // anderer Kartenstil (ADR-0055)
        }
    }

    /// <summary>Kartenstil gewechselt: Namensnennung, Hinweis und Kacheln neu (UI-Thread).</summary>
    public event EventHandler? SourceChanged;

    /// <summary>
    /// Stilwechsel: Speicher, Fehlversuche und Warteschlange verwerfen, laufende Abrufe des alten Stils ignorieren und die
    /// zuletzt sichtbaren Kacheln mit dem neuen Stil anfordern (die Karte meldet bei gleichem Ausschnitt nicht erneut).
    /// </summary>
    public void Reset()
    {
        IReadOnlyList<MapTileId> wanted;
        lock (_gate)
        {
            _generation++;
            _memory.Clear();
            _lru.Clear();
            _failed.Clear();
            _running.Clear();
            _queue.Clear();
            wanted = _lastWanted;
        }

        PostToUi(() =>
        {
            SourceChanged?.Invoke(this, EventArgs.Empty);
            StateChanged?.Invoke(this, EventArgs.Empty);
            TilesChanged?.Invoke(this, EventArgs.Empty);
        });
        SetWanted(wanted);
    }

    public MapTileSourceState State => _source.State;

    /// <summary>Die Kachelquelle (Stil, Namensnennung).</summary>
    public IMapTileSource Source => _source;

    public string Attribution => _source.Attribution;

    public int MaxZoom => _source.MaxZoom;

    /// <summary>Neue Kacheln liegen im Speicher (UI-Thread, gebündelt).</summary>
    public event EventHandler? TilesChanged;

    /// <summary>Zustand der Quelle geändert (UI-Thread).</summary>
    public event EventHandler? StateChanged;

    /// <summary>Kachel aus dem Speicher oder null.</summary>
    public ImageSource? TryGet(MapTileId id)
    {
        lock (_gate)
        {
            if (_memory.TryGetValue(id, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                return node.Value.Image;
            }

            return null;
        }
    }

    /// <summary>Sichtbare Kacheln in Ladereihenfolge (Mitte zuerst); ersetzt die Warteschlange.</summary>
    public void SetWanted(IReadOnlyList<MapTileId> tiles)
    {
        _lastWanted = tiles;
        if (_source.State == MapTileSourceState.Disabled)
        {
            return;
        }

        lock (_gate)
        {
            var queue = new List<MapTileId>(tiles.Count);
            foreach (var id in tiles)
            {
                if (!_memory.ContainsKey(id) && !_running.Contains(id)
                    && !(_failed.TryGetValue(id, out var failedAt) && DateTime.UtcNow - failedAt < FailedRetry))
                {
                    queue.Add(id);
                }
            }

            _queue = queue;
        }

        Pump();
    }

    private void Pump()
    {
        while (true)
        {
            MapTileId id;
            int generation;
            lock (_gate)
            {
                if (_inFlight >= _parallel || _queue.Count == 0)
                {
                    return;
                }

                id = _queue[0];
                _queue.RemoveAt(0);
                _running.Add(id);
                _inFlight++;
                generation = _generation;
            }

            _ = Task.Run(() => LoadAsync(id, generation));
        }
    }

    private async Task LoadAsync(MapTileId id, int generation)
    {
        ImageSource? image = null;
        try
        {
            var bytes = await _source.GetTileAsync(id, CancellationToken.None).ConfigureAwait(false);
            image = bytes is null ? null : Decode(bytes);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Kartenkachel {Tile} nicht geladen", id);
        }

        lock (_gate)
        {
            _inFlight--;
            if (generation != _generation)
            {
                image = null; // Kachel des vorigen Stils: verwerfen (kein Eintrag, kein Fehlversuch), nur den Platz freigeben
            }
            else
            {
                _running.Remove(id);
                if (image is null)
                {
                    _failed[id] = DateTime.UtcNow;
                }
                else if (!_memory.ContainsKey(id))
                {
                    _failed.Remove(id);
                    _memory[id] = _lru.AddFirst((id, image));
                    while (_memory.Count > MemoryCapacity && _lru.Last is { } last)
                    {
                        _lru.RemoveLast();
                        _memory.Remove(last.Value.Id);
                    }
                }
            }
        }

        if (image is not null && Interlocked.CompareExchange(ref _flushScheduled, 1, 0) == 0)
        {
            PostToUi(() =>
            {
                Interlocked.Exchange(ref _flushScheduled, 0);
                TilesChanged?.Invoke(this, EventArgs.Empty);
            });
        }

        Pump();
    }

    private static ImageSource? Decode(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.StreamSource = stream;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or InvalidOperationException or ArgumentException or FileFormatException)
        {
            return null;
        }
    }

    private static void PostToUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            action();
            return;
        }

        _ = dispatcher.InvokeAsync(action, DispatcherPriority.Background);
    }
}
