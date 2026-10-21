using System;
using System.Collections.Generic;
using System.Globalization;

namespace PhotoViewerV3.Places.Map.Tiles;

/// <summary>Kartenstil der Seite „Orte“ (ADR-0055); gespeichert als Name in der Settings-Tabelle.</summary>
public enum MapStyle
{
    /// <summary>Standard: hell CARTO Positron, dunkel CARTO Dark Matter — folgt Lumoras Farbschema live.</summary>
    Automatic = 0,

    /// <summary>CARTO Voyager (farbig, Straßen und Grünflächen betont).</summary>
    Voyager = 1,

    /// <summary>CARTO Positron (hell, zurückhaltend grau).</summary>
    Positron = 2,

    /// <summary>CARTO Dark Matter (dunkel).</summary>
    DarkMatter = 3,

    /// <summary>OpenStreetMap-Standard (tile.openstreetmap.org, wie bis 0.4.0).</summary>
    OpenStreetMap = 4,

    /// <summary>Satellit über MapTiler (nur mit eigenem MapTiler-Schlüssel).</summary>
    Satellite = 5,
}

/// <summary>Anbieter einer Kachelquelle — bestimmt Schlüssel, Cache-Regeln und Namensnennung.</summary>
public enum MapTileVendor
{
    OpenStreetMap = 0,
    Carto = 1,
    MapTiler = 2,
}

/// <summary>
/// Eine konkrete Kachelquelle (ADR-0055). <see cref="UrlTemplate"/> enthält <c>{z}</c>, <c>{x}</c>, <c>{y}</c> und — bei
/// Anbietern mit Schlüssel — <c>{key}</c>. <see cref="CacheFolder"/> = Unterordner unter <c>&lt;CacheRoot&gt;\maptiles</c>
/// (je Stil getrennt). <see cref="MaxCacheAge"/> = Alterungsgrenze laut Nutzungsbedingungen (CARTO: 30 Tage; null = nur
/// HTTP-Ablaufzeit wie bei OSM).
/// </summary>
public sealed record MapTileStyleDefinition(
    string Id,
    string Name,
    MapTileVendor Vendor,
    string UrlTemplate,
    string Attribution,
    string AttributionLink,
    int MaxZoom,
    TimeSpan? MaxCacheAge,
    int MaxParallelDownloads)
{
    /// <summary>Braucht die Quelle einen Schlüssel des Benutzers?</summary>
    public bool RequiresKey => Vendor != MapTileVendor.OpenStreetMap;

    /// <summary>
    /// Kachel-URL. Ohne Schlüssel bei einer Quelle mit Schlüsselpflicht: null (nie unbeschlüsselt abrufen — CARTO setzt
    /// sonst ein Wasserzeichen, ADR-0055). Der Schlüssel wird URL-kodiert eingesetzt und nie protokolliert.
    /// </summary>
    public string? BuildUrl(MapTileId id, string? apiKey)
    {
        var key = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        if (RequiresKey && key is null)
        {
            return null;
        }

        return UrlTemplate
            .Replace("{z}", id.Zoom.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{x}", id.X.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{y}", id.Y.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{key}", key is null ? string.Empty : Uri.EscapeDataString(key), StringComparison.Ordinal);
    }
}

/// <summary>Schlüssel des Benutzers (Einstellungen › Darstellung › Karte); leer = keiner.</summary>
public sealed record MapApiKeys(string? Carto, string? MapTiler)
{
    public static MapApiKeys None { get; } = new(null, null);

    public bool HasCarto => !string.IsNullOrWhiteSpace(Carto);

    public bool HasMapTiler => !string.IsNullOrWhiteSpace(MapTiler);

    /// <summary>Schlüssel für einen Anbieter (null bei OSM bzw. ohne Schlüssel).</summary>
    public string? For(MapTileVendor vendor) => vendor switch
    {
        MapTileVendor.Carto => HasCarto ? Carto!.Trim() : null,
        MapTileVendor.MapTiler => HasMapTiler ? MapTiler!.Trim() : null,
        _ => null,
    };
}

/// <summary>
/// Ergebnis der Stilwahl: die tatsächlich genutzte Quelle, der Schlüssel dazu und — beim Rückfall auf OSM, weil der
/// Schlüssel fehlt — ein Hinweis für die Karte.
/// </summary>
public sealed record MapStyleResolution(MapStyle Requested, MapTileStyleDefinition Definition, string? ApiKey, string? Notice)
{
    public bool IsFallback => Notice is not null;
}

/// <summary>Eintrag der Stilauswahl (Einstellungen, Stil-Menü der Karte).</summary>
public sealed record MapStyleOption(MapStyle Style, string Label, string Description, MapTileVendor? Vendor);

/// <summary>
/// Katalog der Kartenstile (ADR-0055). CARTO-Raster laut carto.com/basemaps (Schlüssel als Abfrageparameter <c>key</c>,
/// Namensnennung „© OpenStreetMap-Mitwirkende, © CARTO“), MapTiler-Satellit als 256-px-Raster mit Schlüssel <c>key</c>
/// (Namensnennung laut maptiler.com/copyright), OSM unverändert wie in ADR-0051.
/// </summary>
public static class MapStyleCatalog
{
    /// <summary>Hinweis auf der Karte, wenn ein Stil mangels Schlüssel auf OpenStreetMap zurückfällt.</summary>
    public const string KeyMissingNotice = "API-Schlüssel nötig – in den Einstellungen eintragen";

    public const string CartoAttribution = "© OpenStreetMap-Mitwirkende, © CARTO";

    public const string MapTilerAttribution = "© MapTiler © OpenStreetMap-Mitwirkende · Satellitenbilder: © MapTiler, Maxar, Copernicus u. a.";

    /// <summary>Bedingungen von CARTO: Kacheln höchstens 30 Tage im Cache halten.</summary>
    public static readonly TimeSpan CartoMaxCacheAge = TimeSpan.FromDays(30);

    /// <summary>MapTiler erlaubt nur einen „temporären“ persönlichen Cache ohne Frist — Lumora hält dieselben 30 Tage.</summary>
    public static readonly TimeSpan MapTilerMaxCacheAge = TimeSpan.FromDays(30);

    public static MapTileStyleDefinition OpenStreetMap { get; } = new(
        "osm", "Standard (OpenStreetMap)", MapTileVendor.OpenStreetMap,
        "https://tile.openstreetmap.org/{z}/{x}/{y}.png",
        OsmTileSource.OsmAttribution, "https://www.openstreetmap.org/copyright", 19, null, 2);

    public static MapTileStyleDefinition Voyager { get; } = Carto("carto-voyager", "Voyager", "voyager");

    public static MapTileStyleDefinition Positron { get; } = Carto("carto-positron", "Positron", "light_all");

    public static MapTileStyleDefinition DarkMatter { get; } = Carto("carto-darkmatter", "Dark Matter", "dark_all");

    public static MapTileStyleDefinition Satellite { get; } = new(
        "maptiler-satellite", "Satellit (MapTiler)", MapTileVendor.MapTiler,
        "https://api.maptiler.com/maps/satellite/256/{z}/{x}/{y}.jpg?key={key}",
        MapTilerAttribution, "https://www.maptiler.com/copyright/", 19, MapTilerMaxCacheAge, 2);

    /// <summary>Alle konkreten Quellen (Cache-Ordner, Alterung beim Start).</summary>
    public static IReadOnlyList<MapTileStyleDefinition> All { get; } = [OpenStreetMap, Voyager, Positron, DarkMatter, Satellite];

    /// <summary>Auswahl in Einstellungen und Stil-Menü, in Anzeigereihenfolge.</summary>
    public static IReadOnlyList<MapStyleOption> Options { get; } =
    [
        new(MapStyle.Automatic, "Automatisch", "Hell: CARTO Positron, dunkel: CARTO Dark Matter — folgt dem Farbschema von Lumora", MapTileVendor.Carto),
        new(MapStyle.Voyager, "Voyager", "CARTO Voyager — farbig, Straßen und Grünflächen", MapTileVendor.Carto),
        new(MapStyle.Positron, "Positron", "CARTO Positron — hell und zurückhaltend", MapTileVendor.Carto),
        new(MapStyle.DarkMatter, "Dark Matter", "CARTO Dark Matter — dunkel", MapTileVendor.Carto),
        new(MapStyle.OpenStreetMap, "Standard (OpenStreetMap)", "OpenStreetMap-Standardkarte, ohne Schlüssel", null),
        new(MapStyle.Satellite, "Satellit", "Satellitenbilder über MapTiler", MapTileVendor.MapTiler),
    ];

    /// <summary>
    /// Stil → Quelle. „Automatisch“: hell Positron, dunkel Dark Matter (ohne CARTO-Schlüssel still OpenStreetMap, weil das
    /// der Standard ist). Ausdrücklich gewählte CARTO-/Satellit-Stile ohne Schlüssel fallen mit Hinweis auf OSM zurück —
    /// nie ein Abruf ohne Schlüssel.
    /// </summary>
    public static MapStyleResolution Resolve(MapStyle style, bool isDark, MapApiKeys keys)
    {
        var definition = style switch
        {
            MapStyle.Voyager => Voyager,
            MapStyle.Positron => Positron,
            MapStyle.DarkMatter => DarkMatter,
            MapStyle.OpenStreetMap => OpenStreetMap,
            MapStyle.Satellite => Satellite,
            _ => isDark ? DarkMatter : Positron,
        };

        if (!definition.RequiresKey)
        {
            return new MapStyleResolution(style, definition, null, null);
        }

        var key = keys.For(definition.Vendor);
        if (key is not null)
        {
            return new MapStyleResolution(style, definition, key, null);
        }

        // Automatisch ohne Schlüssel = bisheriges Verhalten (OSM) ohne Dauerhinweis; der Hinweis steht im Stil-Menü.
        return new MapStyleResolution(style, OpenStreetMap, null, style == MapStyle.Automatic ? null : KeyMissingNotice);
    }

    /// <summary>Stil aus dem gespeicherten Namen (unbekannt/leer → Automatisch).</summary>
    public static MapStyle Parse(string? value)
        => Enum.TryParse<MapStyle>(value, ignoreCase: true, out var style) && Enum.IsDefined(style) ? style : MapStyle.Automatic;

    private static MapTileStyleDefinition Carto(string id, string name, string variant) => new(
        id, name, MapTileVendor.Carto,
        $"https://basemaps.cartocdn.com/rastertiles/{variant}/{{z}}/{{x}}/{{y}}.png?key={{key}}",
        CartoAttribution, "https://carto.com/attribution", 19, CartoMaxCacheAge, 2);
}
