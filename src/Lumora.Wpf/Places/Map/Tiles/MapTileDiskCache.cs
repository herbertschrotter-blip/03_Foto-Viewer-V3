using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumora.Places.Map.Tiles;

/// <summary>
/// Platten-Cache der Kartenkacheln (ADR-0051) unter <c>&lt;CacheRoot&gt;\maptiles\&lt;stil&gt;\z\x\y.png</c> (je Stil getrennt,
/// ADR-0055; Dateiname immer <c>.png</c>, auch für JPEG-Kacheln) mit Größengrenze (LRU über alle Stile unter
/// <c>maptiles</c>). Quellen mit Alterungsgrenze (CARTO, MapTiler: 30 Tage) verwerfen ältere Kacheln beim Lesen
/// (Abrufzeit = Erstellungszeit der Datei, beim Schreiben ausdrücklich gesetzt); <see cref="SweepExpired"/> löscht sie
/// auch, wenn der Stil gerade nicht benutzt wird.
/// Ablaufzeit = Änderungszeit der Datei (beim Schreiben auf „läuft ab um“ gesetzt), letzte Nutzung = Zugriffszeit
/// (beim Lesen ausdrücklich gesetzt — Windows aktualisiert sie sonst nicht zuverlässig). Überschreitet der Cache die
/// Grenze, löscht ein Hintergrundlauf die am längsten nicht benutzten Kacheln bis auf 90 %. Leerer Ordner = kein
/// Platten-Cache (versteckter Modus ohne Container-Cache: nur Speicher).
/// </summary>
public sealed class MapTileDiskCache
{
    /// <summary>Unterordner unter dem Cache-Ordner der App.</summary>
    public const string RootFolderName = "maptiles";

    private readonly string? _root;
    private readonly string? _folder;
    private readonly TimeSpan? _maxAge;
    private readonly long _maxBytes;
    private readonly ILogger _logger;
    private long _knownBytes = -1; // -1 = noch nicht gezählt
    private int _trimming;
    private readonly bool _trimInBackground;

    /// <param name="trimInBackground">false nur in Tests: Aufräumen ausschließlich über <see cref="TrimNow"/>.</param>
    /// <param name="styleFolder">Unterordner des Stils (<see cref="MapTileStyleDefinition.Id"/>); null = direkt unter <c>maptiles</c>.</param>
    /// <param name="maxAge">Alterungsgrenze laut Nutzungsbedingungen (null = keine).</param>
    public MapTileDiskCache(string? cacheRoot, long maxBytes, ILogger? logger = null, bool trimInBackground = true, string? styleFolder = null, TimeSpan? maxAge = null)
    {
        _trimInBackground = trimInBackground;
        _root = string.IsNullOrWhiteSpace(cacheRoot) ? null : Path.Combine(cacheRoot, RootFolderName);
        _folder = _root is null || string.IsNullOrWhiteSpace(styleFolder) ? _root : Path.Combine(_root, styleFolder);
        _maxAge = maxAge;
        _maxBytes = maxBytes;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Ordner der Kacheln dieses Stils oder null (nur Speicher).</summary>
    public string? Folder => _folder;

    /// <summary>Kachel aus dem Cache (auch abgelaufene, mit <c>IsFresh = false</c>) oder null — über der Alterungsgrenze gelöscht.</summary>
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

            if (_maxAge is { } maxAge && nowUtc - info.CreationTimeUtc > maxAge)
            {
                info.Delete(); // Bedingungen des Anbieters: nicht länger als die Grenze vorhalten
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
            File.SetCreationTimeUtc(path, nowUtc); // Abrufzeit (Alterungsgrenze; Windows übernähme sonst die alte Zeit)
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
        => _folder is null ? null : Path.Combine(_folder, id.Zoom.ToString(), id.X.ToString(), id.Y + ".png");

    /// <summary>
    /// Bis 0.4.0 lagen die OSM-Kacheln direkt unter <c>maptiles\z\…</c>; seit 0.4.1 je Stil (ADR-0055). Verschiebt die
    /// Zoom-Ordner einmalig nach <c>maptiles\&lt;osmFolder&gt;</c>, damit vorhandene Kacheln weiter genutzt werden (keine
    /// erneuten OSM-Abrufe). Gibt die Zahl verschobener Ordner zurück.
    /// </summary>
    public static int MigrateLegacyLayout(string? cacheRoot, string osmFolder, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(cacheRoot))
        {
            return 0;
        }

        var root = Path.Combine(cacheRoot, RootFolderName);
        if (!Directory.Exists(root))
        {
            return 0;
        }

        var moved = 0;
        foreach (var dir in new DirectoryInfo(root).EnumerateDirectories())
        {
            if (!int.TryParse(dir.Name, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _))
            {
                continue; // Stil-Ordner
            }

            var target = Path.Combine(root, osmFolder, dir.Name);
            try
            {
                if (Directory.Exists(target))
                {
                    dir.Delete(recursive: true); // neuere Kacheln gibt es schon — alte verwerfen
                }
                else
                {
                    Directory.CreateDirectory(Path.Combine(root, osmFolder));
                    dir.MoveTo(target);
                }

                moved++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger?.LogDebug(ex, "Alter Kachel-Ordner nicht übernommen");
            }
        }

        if (moved > 0)
        {
            logger?.LogInformation("Kartenkachel-Cache: {Moved} Zoom-Ordner in den Stil-Ordner „{Folder}“ übernommen", moved, osmFolder);
        }

        return moved;
    }

    /// <summary>
    /// Löscht in den Cache-Ordnern aller Stile mit Alterungsgrenze die Kacheln, die älter als die Grenze sind (CARTO-
    /// Bedingungen: höchstens 30 Tage) — auch für gerade nicht benutzte Stile. Gibt die Zahl gelöschter Kacheln zurück.
    /// </summary>
    public static int SweepExpired(string? cacheRoot, IEnumerable<MapTileStyleDefinition> styles, DateTime nowUtc, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(cacheRoot))
        {
            return 0;
        }

        var removed = 0;
        foreach (var style in styles)
        {
            var folder = Path.Combine(cacheRoot, RootFolderName, style.Id);
            if (style.MaxCacheAge is not { } maxAge || !Directory.Exists(folder))
            {
                continue;
            }

            foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*.png", SearchOption.AllDirectories))
            {
                try
                {
                    if (nowUtc - file.CreationTimeUtc > maxAge)
                    {
                        file.Delete();
                        removed++;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger?.LogDebug(ex, "Gealterte Kartenkachel nicht gelöscht");
                }
            }
        }

        if (removed > 0)
        {
            logger?.LogInformation("Kartenkachel-Cache: {Removed} Kacheln über der Alterungsgrenze gelöscht", removed);
        }

        return removed;
    }
}
