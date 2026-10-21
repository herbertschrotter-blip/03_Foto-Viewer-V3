using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.Logging;
using Lumora.Services.Places;

namespace Lumora.Gallery.Places;

/// <summary>Anfrage an den Dialog „Ort setzen“.</summary>
/// <param name="Subject">„Ausgewählte Datei“ bzw. „Auswahl“.</param>
/// <param name="Current">Ort der ersten Datei (Vorbelegung), null ohne.</param>
/// <param name="Recent">Zuletzt verwendete bzw. häufige Orte des Bibliotheksbereichs.</param>
/// <param name="HasVideos">Auswahl enthält Videos (Hinweis: nur Katalog).</param>
public sealed record PlaceDialogRequest(string Subject, int FileCount, FileGeoInfo? Current, IReadOnlyList<RecentLocation> Recent, bool HasVideos = false);

/// <summary>Antwort des Dialogs: setzen (Koordinate) oder entfernen.</summary>
public sealed record PlaceDialogResult(double? Latitude, double? Longitude, bool Remove, bool KeepModifiedTime)
{
    public static PlaceDialogResult Set(double latitude, double longitude, bool keepModifiedTime = true) => new(latitude, longitude, false, keepModifiedTime);

    public static PlaceDialogResult Clear() => new(null, null, true, true);
}

/// <summary>Zeigt den Dialog „Ort setzen“ (austauschbar: Rauchtest-Fake).</summary>
public interface IPlaceDialogService
{
    /// <summary>Dialog modal zeigen; null bei Abbruch.</summary>
    Task<PlaceDialogResult?> PickAsync(PlaceDialogRequest request, Window? owner);
}

/// <inheritdoc cref="IPlaceDialogService"/>
public sealed class PlaceDialogService : IPlaceDialogService
{
    private readonly IReverseGeocoder _geocoder;
    private readonly IMapPicker? _mapPicker;

    public PlaceDialogService(IReverseGeocoder geocoder, IMapPicker? mapPicker = null)
    {
        _geocoder = geocoder;
        _mapPicker = mapPicker;
    }

    public Task<PlaceDialogResult?> PickAsync(PlaceDialogRequest request, Window? owner)
        => Task.FromResult(SetPlaceDialog.Show(owner ?? Application.Current?.MainWindow, new SetPlaceViewModel(request, _geocoder, _mapPicker)));
}

/// <summary>
/// „Ort setzen…“ (ADR-0050) für die Galerie-Auswahl: Dialog zeigen, über <see cref="IGeoTagService"/> schreiben (Katalog +
/// EXIF bzw. XMP), Ergebnis als Text samt Fehlerliste. Rückgängig über <see cref="Undo"/>.
/// </summary>
public sealed class PlaceActions
{
    private const int RecentCount = 8;

    private readonly IPlaceDialogService _dialog;
    private readonly ILogger<PlaceActions> _logger;

    public PlaceActions(
        IGeoTagService service,
        IGeoTagUndoService undo,
        IGeoCatalogReader reader,
        IPlaceLookup places,
        IPlaceDialogService dialog,
        ILogger<PlaceActions> logger)
    {
        Service = service;
        Undo = undo;
        Reader = reader;
        Places = places;
        _dialog = dialog;
        _logger = logger;
    }

    public IGeoTagService Service { get; }

    public IGeoTagUndoService Undo { get; }

    public IGeoCatalogReader Reader { get; }

    public IPlaceLookup Places { get; }

    /// <summary>Für ausgewählte Dateien des Bibliotheksbereichs; null bei Abbruch.</summary>
    public async Task<PlaceActionResult?> SetForFilesAsync(IReadOnlyList<int> fileIds, IReadOnlyCollection<int> libraryIds, bool hasVideos, Window? owner)
    {
        if (fileIds.Count == 0)
        {
            return null;
        }

        var current = await Reader.GetAsync(fileIds[0]).ConfigureAwait(true);
        var recent = await Reader.GetRecentLocationsAsync(libraryIds, RecentCount).ConfigureAwait(true);
        var subject = fileIds.Count == 1 ? "Ausgewählte Datei" : "Auswahl";
        var choice = await _dialog.PickAsync(new PlaceDialogRequest(subject, fileIds.Count, current, recent, hasVideos), owner).ConfigureAwait(true);
        if (choice is null)
        {
            return null;
        }

        GeoTagResult result;
        string verb;
        if (choice.Remove)
        {
            verb = "Ort entfernt";
            result = await Service.ClearLocationAsync(fileIds, CancellationToken.None).ConfigureAwait(true);
        }
        else
        {
            verb = "Ort gesetzt";
            result = await Service.SetLocationAsync(fileIds, choice.Latitude!.Value, choice.Longitude!.Value, choice.KeepModifiedTime, CancellationToken.None).ConfigureAwait(true);
        }

        _logger.LogInformation("{Verb}: {Written} geschrieben, {CatalogOnly} nur Katalog, {Failed} Fehler", verb, result.Written, result.CatalogOnly, result.Failed);
        return new PlaceActionResult(Describe(verb, result), result);
    }

    /// <summary>„Ort gesetzt: 12 Datei(en) (3 nur im Katalog), 1 fehlgeschlagen — Rückgängig möglich.“</summary>
    public static string Describe(string verb, GeoTagResult result)
    {
        var changed = result.Written + result.CatalogOnly;
        var text = $"{verb}: {changed:N0} Datei(en)";
        if (result.CatalogOnly > 0)
        {
            text += $" ({result.CatalogOnly:N0} nur im Katalog)";
        }

        if (result.Failed > 0)
        {
            text += $", {result.Failed:N0} fehlgeschlagen";
        }

        return changed > 0 ? text + " — Rückgängig möglich." : text + ".";
    }
}

/// <summary>Ergebnistext und Einzelheiten (Fehlerliste für den Dialog).</summary>
public sealed record PlaceActionResult(string Text, GeoTagResult Result);
