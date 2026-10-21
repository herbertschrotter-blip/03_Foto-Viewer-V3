using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Places.Map.Tiles;

namespace Lumora.Places.Styles;

/// <summary>Ergebnis von „Schlüssel prüfen“: <see cref="IsValid"/> null = nicht geprüft (abgeschaltet, kein Netz).</summary>
public sealed record MapKeyCheckResult(bool? IsValid, string Message);

/// <summary>„Schlüssel prüfen“ (ADR-0055): genau ein Kachelabruf mit dem eingetragenen Schlüssel.</summary>
public interface IMapKeyValidator
{
    Task<MapKeyCheckResult> CheckAsync(MapTileVendor vendor, string? apiKey, CancellationToken ct = default);
}

/// <summary>
/// Ruft eine einzelne, grobe Kachel ab (CARTO Positron bzw. MapTiler-Satellit, Zoom 1) — kein Cache, keine weiteren
/// Abrufe. Bei abgeschalteter Kachelquelle (Tests, Rauchtest) wird nichts abgerufen. Die URL mit Schlüssel wird nie
/// protokolliert.
/// </summary>
public sealed class MapKeyValidator : IMapKeyValidator
{
    private static readonly MapTileId ProbeTile = new(1, 1, 0);

    private readonly MapTileOptions _options;
    private readonly string _userAgent;
    private readonly HttpMessageHandler? _handler;
    private readonly ILogger _logger;
    private readonly IMapTileUsageCounter? _usage;

    /// <param name="handler">Nur Tests: eigener Handler (kein Netz).</param>
    public MapKeyValidator(MapTileOptions options, string userAgent, HttpMessageHandler? handler = null, ILogger? logger = null, IMapTileUsageCounter? usage = null)
    {
        _usage = usage;
        _options = options;
        _userAgent = userAgent;
        _handler = handler;
        _logger = logger ?? NullLogger.Instance;
    }

    public async Task<MapKeyCheckResult> CheckAsync(MapTileVendor vendor, string? apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new MapKeyCheckResult(false, "Kein Schlüssel eingetragen.");
        }

        var style = vendor switch
        {
            MapTileVendor.Carto => MapStyleCatalog.Positron,
            MapTileVendor.MapTiler => MapStyleCatalog.Satellite,
            _ => null,
        };
        if (style?.BuildUrl(ProbeTile, apiKey) is not { } url)
        {
            return new MapKeyCheckResult(null, "Für diesen Anbieter gibt es keinen Schlüssel.");
        }

        if (!_options.Enabled)
        {
            return new MapKeyCheckResult(null, "Kartenkacheln sind abgeschaltet (Test/Rauchtest) — nicht geprüft.");
        }

        using var http = _handler is null ? new HttpClient() : new HttpClient(_handler, disposeHandler: false);
        http.Timeout = TimeSpan.FromSeconds(15);
        http.DefaultRequestHeaders.UserAgent.ParseAdd(_userAgent);
        try
        {
            _usage?.Count(vendor); // auch die Prüfung ist ein Abruf beim Anbieter
            using var response = await http.GetAsync(url, ct).ConfigureAwait(false);
            var isImage = response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true;
            _logger.LogInformation("Karten-Schlüssel ({Vendor}) geprüft: {Status}", vendor, (int)response.StatusCode);
            if (response.IsSuccessStatusCode && isImage)
            {
                return new MapKeyCheckResult(true, "Schlüssel gültig — eine Kachel wurde geladen.");
            }

            return response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? new MapKeyCheckResult(false, $"Schlüssel abgelehnt ({(int)response.StatusCode}) — bitte prüfen.")
                : new MapKeyCheckResult(false, $"Der Kartenserver antwortet {(int)response.StatusCode} — später erneut prüfen.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _logger.LogInformation("Karten-Schlüssel ({Vendor}) nicht prüfbar: {Error}", vendor, ex.GetType().Name);
            return new MapKeyCheckResult(null, "Kartenserver nicht erreichbar — keine Internetverbindung?");
        }
    }
}
