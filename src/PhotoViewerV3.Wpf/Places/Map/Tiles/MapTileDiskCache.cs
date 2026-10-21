using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PhotoViewerV3.Places.Map.Tiles;

/// <summary>
/// Platten-Cache der Kartenkacheln (ADR-0051) unter <c>&lt;CacheRoot&gt;\maptiles\z\x\y.png</c> mit Größengrenze (LRU).
/// Ablaufzeit = Änderungszeit der Datei (beim Schreiben auf „läuft ab um“ gesetzt), letzte Nutzung = Zugriffszeit
/// (beim Lesen ausdrücklich gesetzt — Windows aktualisiert sie sonst nicht zuverlässig). Überschreitet der Cache die
/// Grenze, löscht ein Hintergrundlauf die am längsten nicht benutzten Kacheln bis auf 90 %. Leerer Ordner = kein
/// Platten-Cache (versteckter Modus ohne Container-Cache: nur Speicher).
/// </summary>
public sealed class MapTileDiskCache
{
    private readonly string? _root;
    private readonly long _maxBytes;
    private readonly ILogger _logger;
    private long _knownBytes = -1; // -1 = noch nicht gezählt
    private int _trimming;
    private readonly bool _trimInBackground;

    /// <param name="trimInBackground">false nur in Tests: Aufräumen ausschließlich über <see cref="TrimNow"/>.</param>
    public MapTileDiskCache(string? cacheRoot, long maxBytes, ILogger? logger = null, bool trimInBackground = true)
    {
        _trimInBackground = trimInBackground;
        _root = string.IsNullOrWhiteSpace(cacheRoot) ? null : Path.Combine(cacheRoot, "maptiles");
        _maxBytes = maxBytes;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Ordner der Kacheln oder null (nur Speicher).</summary>
    public string? Folder => _root;

    /// <summary>Kachel aus dem Cache (auch abgelaufene, mit <c>IsFresh = false</c>) oder null.</summary>
    public (byte[] Bytes, bool IsFresh)? TryRead(MapTileId id, DateTime nowUtc)
    {
        if (PathOf(id) is not { } path)
        {
            return null;
        }

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return null;
            }

            var bytes = File.ReadAllBytes(path);
            File.SetLastAccessTimeUtc(path, nowUtc);
            return (bytes, info.LastWriteTimeUtc > nowUtc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Kartenkachel {Tile} nicht lesbar", id);
            return null;
        }
    }

    /// <summary>Speichert eine Kachel mit Ablaufzeit; startet bei Überschreiten der Grenze das Aufräumen.</summary>
    public void Write(MapTileId id, byte[] bytes, DateTime expiresUtc, DateTime nowUtc)
    {
        if (PathOf(id) is not { } path)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var old = File.Exists(path) ? new FileInfo(path).Length : 0;
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
            File.SetLastWriteTimeUtc(path, expiresUtc);
            File.SetLastAccessTimeUtc(path, nowUtc);
            if (Interlocked.Read(ref _knownBytes) >= 0)
            {
                Interlocked.Add(ref _knownBytes, bytes.Length - old);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Kartenkachel {Tile} nicht gespeichert", id);
            return;
        }

        if (Interlocked.Read(ref _knownBytes) < 0 || Interlocked.Read(ref _knownBytes) > _maxBytes)
        {
            StartTrim();
        }
    }

    /// <summary>Zählt den Cache und löscht bei Bedarf (LRU) — synchron; für Tests und den Hintergrundlauf.</summary>
    public long TrimNow()
    {
        if (_root is null || !Directory.Exists(_root))
        {
            Interlocked.Exchange(ref _knownBytes, 0);
            return 0;
        }

        var files = new DirectoryInfo(_root).EnumerateFiles("*.png", SearchOption.AllDirectories).ToList();
        var total = files.Sum(f => f.Length);
        if (total > _maxBytes)
        {
            var target = (long)(_maxBytes * 0.9);
            var removed = 0;
            foreach (var file in files.OrderBy(f => f.LastAccessTimeUtc))
            {
                if (total <= target)
                {
                    break;
                }

                try
                {
                    var length = file.Length;
                    file.Delete();
                    total -= length;
                    removed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogDebug(ex, "Kartenkachel nicht gelöscht");
                }
            }

            _logger.LogInformation("Kartenkachel-Cache aufgeräumt: {Removed} Kacheln entfernt, {Size:N0} Bytes übrig", removed, total);
        }

        Interlocked.Exchange(ref _knownBytes, total);
        return total;
    }

    private void StartTrim()
    {
        if (!_trimInBackground || Interlocked.CompareExchange(ref _trimming, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                TrimNow();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Kartenkachel-Cache konnte nicht aufgeräumt werden");
            }
            finally
            {
                Interlocked.Exchange(ref _trimming, 0);
            }
        });
    }

    private string? PathOf(MapTileId id)
        => _root is null ? null : Path.Combine(_root, id.Zoom.ToString(), id.X.ToString(), id.Y + ".png");
}
