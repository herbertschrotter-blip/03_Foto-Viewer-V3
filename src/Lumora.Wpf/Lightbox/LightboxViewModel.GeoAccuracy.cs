using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Lumora.Places.Suggestions;
using Lumora.Services.Messaging;
using Lumora.Services.Places;
using Lumora.Services.Places.Accuracy;
using Lumora.Services.Places.Suggestions;

namespace Lumora.Lightbox;

/// <summary>
/// Hinweis „Position geschätzt“ in der Info-Leiste der Lightbox (ADR-0062, Mockup 10): Begründung (z. B. nur Breite/Länge
/// ohne GPS-Höhe/-Zeit), Vorschlag mit Quelle („Laut Ihrer GPS-Spur … ≈ 4,8 km westlich“) und die Knöpfe „Vorschlag
/// übernehmen“, „Auf der Karte zeigen“, „Ort setzen…“. Auch für Fotos ohne Ort, wenn es einen Vorschlag gibt.
/// </summary>
public sealed partial class LightboxViewModel
{
    private PlacesSuggestionContext? _geoSuggestions;
    private GeoSuggestionItem? _geoHintItem;
    private int _geoHintVersion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGeoHint))]
    private string? _geoHintTitle;

    [ObservableProperty] private string? _geoHintText;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyGeoHintCommand))]
    private bool _hasGeoHintSuggestion;

    public bool HasGeoHint => GeoHintTitle is not null;

    /// <summary>„Ort setzen…“ verfügbar (Dialog der Galerie registriert).</summary>
    public bool CanSetPlaceFromHint => _geoSuggestions?.PlaceActions is not null;

    private void InitGeoHint(PlacesSuggestionContext? context) => _geoSuggestions = context;

    private void QueueGeoHint(LightboxItem item)
    {
        if (_geoSuggestions is null)
        {
            return;
        }

        var version = ++_geoHintVersion;
        _ = LoadGeoHintAsync(item, version);
    }

    private async Task LoadGeoHintAsync(LightboxItem item, int version)
    {
        try
        {
            var context = _geoSuggestions!;
            var suggestion = await context.Suggestions.GetForFileAsync(item.FileId).ConfigureAwait(true);
            if (_disposed || version != _geoHintVersion || !ReferenceEquals(item, CurrentItem))
            {
                return;
            }

            _geoHintItem = suggestion;
            if (suggestion is null || (!suggestion.HasStored && suggestion.Suggestion is null))
            {
                GeoHintTitle = null;
                GeoHintText = null;
                HasGeoHintSuggestion = false;
                return;
            }

            GeoHintTitle = suggestion.HasStored ? "Position geschätzt" : "Ohne Ort — Vorschlag vorhanden";
            GeoHintText = DescribeGeoHint(suggestion, context.Geocoder);
            HasGeoHintSuggestion = suggestion.Suggestion is not null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Lightbox: Hinweis zum Ort nicht geladen (FileId {FileId})", item.FileId);
        }
    }

    /// <summary>Begründung + Vorschlag in einem Absatz.</summary>
    internal static string DescribeGeoHint(GeoSuggestionItem item, IReverseGeocoder? geocoder)
    {
        var why = item.HasStored ? GeoAccuracyRules.Explain(item.Reason) : "Das Foto hat keinen Ort.";
        if (item.Suggestion is not { } s || item.Utc is not { } utc)
        {
            return why + " Für die Aufnahmezeit gibt es keine GPS-Spur und kein genaues Foto in der Nähe.";
        }

        var offset = item.LocalTime is { } local ? (int)Math.Round((local - DateTimeOffset.FromUnixTimeSeconds(utc).UtcDateTime).TotalMinutes) : 0;
        var clock = DateTimeOffset.FromUnixTimeSeconds(utc).UtcDateTime.AddMinutes(offset).ToString("HH:mm", System.Globalization.CultureInfo.GetCultureInfo("de-DE"));
        string? place = null;
        try
        {
            place = geocoder?.Nearest(s.Latitude, s.Longitude)?.Name;
        }
        catch (Exception)
        {
            // ohne Ortsnamen
        }

        var where = item.HasStored
            ? $"≈ {GeoMath.FormatDistance(GeoMath.DistanceMeters(item.StoredLatitude!.Value, item.StoredLongitude!.Value, s.Latitude, s.Longitude))} {GeoMath.DirectionText(item.StoredLatitude!.Value, item.StoredLongitude!.Value, s.Latitude, s.Longitude)}"
            : "laut Vorschlag";
        var near = place is null ? string.Empty : $" bei {place}";
        var altitude = s.Altitude is { } alt ? $" ({alt.ToString("#,##0", System.Globalization.CultureInfo.GetCultureInfo("de-DE"))} m)" : string.Empty;
        var source = s.Kind switch
        {
            GeoSuggestionKind.Track => $"Laut Ihrer GPS-Spur („{item.Track?.Title ?? "Aktivität"}“{(item.Track?.Device is { } d ? ", " + d : string.Empty)}) waren Sie um {clock} {where}{near}{altitude}.",
            GeoSuggestionKind.Interpolated => $"Zwischen zwei Fotos mit echtem GPS ({GeoSuggestionText.Source(item, offset)}) lag der Ort um {clock} {where}{near}.",
            _ => $"Ein Foto mit echtem GPS ({GeoSuggestionText.Source(item, offset)}) liegt {where}{near}.",
        };
        return $"{why} {source} Stufe: {GeoSuggestionText.LevelName(s.Level)}.";
    }

    [RelayCommand(CanExecute = nameof(HasGeoHintSuggestion))]
    private async Task ApplyGeoHintAsync()
    {
        if (_geoSuggestions is not { } context || _geoHintItem is not { Suggestion: { } s } item)
        {
            return;
        }

        try
        {
            var settings = await context.Settings.LoadAsync().ConfigureAwait(true);
            var result = await context.Suggestions.ApplyAsync([new GeoSuggestionApply(item.FileId, s.Latitude, s.Longitude, item.SourceOnApply)], settings.SaveToFile).ConfigureAwait(true);
            if (result.Failed > 0)
            {
                GeoHintText = "Übernehmen fehlgeschlagen: " + result.Errors.FirstOrDefault();
                return;
            }

            GeoHintTitle = null;
            GeoHintText = null;
            HasGeoHintSuggestion = false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Lightbox: Vorschlag nicht übernommen");
            GeoHintText = "Übernehmen fehlgeschlagen: " + ex.Message;
        }
    }

    [RelayCommand]
    private void ShowGeoHintOnMap()
    {
        if (_geoHintItem is not { } item)
        {
            return;
        }

        WeakReferenceMessenger.Default.Send(new ShowFileSuggestionOnMapMessage(item.FileId));
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private async Task SetPlaceFromHintAsync()
    {
        if (_geoSuggestions is not { PlaceActions: { } actions } context || _geoHintItem is not { } item)
        {
            return;
        }

        try
        {
            var scope = await context.Scope.GetCurrentAsync().ConfigureAwait(true);
            var owner = Application.Current?.Windows.OfType<LightboxWindow>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow;
            var result = await actions.SetForFilesAsync([item.FileId], scope.LibraryIds, item.MediaType == 1, owner).ConfigureAwait(true);
            if (result is not null && CurrentItem is { } current && current.FileId == item.FileId)
            {
                QueuePlaceRow(current);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Lightbox: Ort setzen fehlgeschlagen");
        }
    }
}
