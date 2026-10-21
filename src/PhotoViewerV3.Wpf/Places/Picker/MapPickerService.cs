using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.Logging;
using PhotoViewerV3.Gallery.Places;
using PhotoViewerV3.Places.Map;
using PhotoViewerV3.Places.Map.Tiles;
using PhotoViewerV3.Services.Places;

namespace PhotoViewerV3.Places.Picker;

/// <summary>
/// <see cref="IMapPicker"/> für den Dialog „Ort setzen…“ von Agent V (ADR-0051): öffnet <see cref="MapPickerWindow"/>
/// modal über dem aktiven Fenster. Start: der übergebene Punkt (Zoom 14), sonst die Ausdehnung der verorteten Fotos des
/// Bereichs plus Rand, sonst die Welt.
/// </summary>
public sealed class MapPickerService : IMapPicker
{
    private readonly MapTileProvider? _tiles;
    private readonly IReverseGeocoder _geocoder;
    private readonly IGeoQueryService _geo;
    private readonly IPlacesScope _scope;
    private readonly ILogger<MapPickerService> _logger;

    public MapPickerService(IReverseGeocoder geocoder, IGeoQueryService geo, IPlacesScope scope, ILogger<MapPickerService> logger, MapTileProvider? tiles = null)
    {
        _geocoder = geocoder;
        _geo = geo;
        _scope = scope;
        _logger = logger;
        _tiles = tiles;
    }

    public async Task<(double Latitude, double Longitude)?> PickAsync(double? initialLatitude, double? initialLongitude, Window? owner)
    {
        var ct = CancellationToken.None;
        const string? title = null;
        GeoBounds? extent = null;
        if (initialLatitude is null || initialLongitude is null)
        {
            try
            {
                extent = await _geo.GetExtentAsync(await _scope.GetLibraryIdsAsync(ct).ConfigureAwait(true), ct).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Ort wählen: Ausdehnung nicht gelesen");
            }
        }

        var window = new MapPickerWindow(_tiles, _geocoder, title);
        owner ??= Application.Current?.Windows is { } windows ? FindActive(windows) : null;
        if (owner is { IsVisible: true })
        {
            window.Owner = owner;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        window.Viewport.SetSize(720, 400);
        if (initialLatitude is { } lat && initialLongitude is { } lon)
        {
            window.Viewport.CenterOn(lat, lon, 14);
            window.SetPoint(lat, lon);
        }
        else
        {
            window.Viewport.FitBounds(extent);
        }

        _ = window.ShowDialog();
        return window.Result;
    }

    private static Window? FindActive(WindowCollection windows)
    {
        foreach (Window w in windows)
        {
            if (w.IsActive)
            {
                return w;
            }
        }

        return Application.Current?.MainWindow;
    }
}
