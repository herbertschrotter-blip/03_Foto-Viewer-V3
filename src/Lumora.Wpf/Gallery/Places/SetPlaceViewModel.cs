using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumora.Services.Metadata;
using Lumora.Services.Places;

namespace Lumora.Gallery.Places;

/// <summary>Treffer der Ortssuche im Dialog.</summary>
public sealed record PlaceSearchHit(PlaceInfo Place, double Latitude, double Longitude)
{
    public string Title => Place.Name;

    public string Detail => string.Join(", ", new[] { Place.Region, Place.Country }.Where(s => !string.IsNullOrWhiteSpace(s)));
}

/// <summary>
/// Logik des Dialogs „Ort setzen“ (ADR-0050): Ortssuche offline (<see cref="IReverseGeocoder.Search"/>), Koordinaten-
/// Eingabe (Dezimalgrad mit Punkt oder Komma, auch „48,2082 16,3738“ in einem Feld oder Grad/Minuten/Sekunden),
/// zuletzt verwendete Orte, optional die Karte (<see cref="IMapPicker"/>), „Ort entfernen“ und „Änderungsdatum
/// beibehalten“ (Standard an). Die Vorschau zeigt den Ort, der aus der Koordinate wird.
/// </summary>
public sealed partial class SetPlaceViewModel : ObservableObject
{
    private const int MaxHits = 12;

    private readonly IReverseGeocoder _geocoder;
    private readonly IMapPicker? _mapPicker;

    public SetPlaceViewModel(PlaceDialogRequest request, IReverseGeocoder geocoder, IMapPicker? mapPicker = null)
    {
        Request = request;
        _geocoder = geocoder;
        _mapPicker = mapPicker;
        foreach (var recent in request.Recent)
        {
            Recent.Add(recent);
        }

        if (request.Current is { Latitude: { } lat, Longitude: { } lon })
        {
            SetCoordinates(lat, lon);
        }
    }

    public PlaceDialogRequest Request { get; }

    public string CountText => Request.FileCount == 1 ? "1 Datei" : $"{Request.FileCount:N0} Dateien";

    /// <summary>„Bisher: Wien, Österreich (…) · EXIF“ bzw. „Bisher: kein Ort“.</summary>
    public string CurrentText => Request.Current is { Text: { } text } current
        ? $"Bisher: {text}{(current.SourceText is { } s ? " · " + s : string.Empty)}{(Request.FileCount > 1 ? " (erste Datei)" : string.Empty)}"
        : "Bisher: kein Ort" + (Request.FileCount > 1 ? " (erste Datei)" : string.Empty);

    public ObservableCollection<PlaceSearchHit> SearchResults { get; } = new();

    public ObservableCollection<RecentLocation> Recent { get; } = new();

    public bool HasRecent => Recent.Count > 0;

    public bool HasSearchResults => SearchResults.Count > 0;

    public bool HasMapPicker => _mapPicker is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAccept), nameof(AcceptText))]
    private bool _isRemove;

    [ObservableProperty] private string? _searchText;

    [ObservableProperty] private PlaceSearchHit? _selectedHit;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAccept), nameof(PreviewText), nameof(ErrorText))]
    private string? _latitudeText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAccept), nameof(PreviewText), nameof(ErrorText))]
    private string? _longitudeText;

    /// <summary>„Änderungsdatum beibehalten“ (Standard an): die Datei behält ihr Änderungsdatum.</summary>
    [ObservableProperty] private bool _keepModifiedTime = true;

    public string AcceptText => IsRemove ? "Ort entfernen" : "Ort setzen";

    public string VideoHint => Request.HasVideos ? "Videos: nur im Katalog (die Datei bleibt unverändert)." : string.Empty;

    /// <summary>Gültige Koordinate aus den beiden Feldern oder null.</summary>
    public GeoCoordinate? Coordinate
    {
        get
        {
            if (TryParseCoordinate(LatitudeText, latitude: true) is not { } lat || TryParseCoordinate(LongitudeText, latitude: false) is not { } lon)
            {
                return null;
            }

            var c = new GeoCoordinate(lat, lon);
            return c.IsValid ? c : null;
        }
    }

    public bool CanAccept => IsRemove || Coordinate is not null;

    /// <summary>„→ Wien, Österreich (48,2082° N, 16,3738° O)“.</summary>
    public string PreviewText => Coordinate is { } c ? "→ " + PlaceText.Describe(_geocoder.Nearest(c.Latitude, c.Longitude), c.Latitude, c.Longitude) : string.Empty;

    public string ErrorText
        => (string.IsNullOrWhiteSpace(LatitudeText) && string.IsNullOrWhiteSpace(LongitudeText)) || Coordinate is not null
            ? string.Empty
            : "Koordinate ungültig — Breite −90 bis 90, Länge −180 bis 180 (z. B. 48,2082 und 16,3738).";

    partial void OnSearchTextChanged(string? value)
    {
        SearchResults.Clear();
        if (!string.IsNullOrWhiteSpace(value) && value.Trim().Length >= 2)
        {
            foreach (var (place, lat, lon) in _geocoder.Search(value, MaxHits))
            {
                SearchResults.Add(new PlaceSearchHit(place, lat, lon));
            }
        }

        OnPropertyChanged(nameof(HasSearchResults));
    }

    partial void OnSelectedHitChanged(PlaceSearchHit? value)
    {
        if (value is not null)
        {
            IsRemove = false;
            SetCoordinates(value.Latitude, value.Longitude);
        }
    }

    partial void OnLatitudeTextChanged(string? value)
    {
        // „48,2082 16,3738“ bzw. „48.2082, 16.3738“ in einem Feld (z. B. aus Google Maps kopiert): aufteilen.
        if (SplitPair(value) is { } pair)
        {
            LatitudeText = pair.Latitude;
            LongitudeText = pair.Longitude;
        }
    }

    /// <summary>Zuletzt verwendeten Ort übernehmen.</summary>
    [RelayCommand]
    private void UseRecent(RecentLocation? recent)
    {
        if (recent is not null)
        {
            IsRemove = false;
            SetCoordinates(recent.Latitude, recent.Longitude);
        }
    }

    [RelayCommand]
    private async Task PickOnMapAsync()
    {
        if (_mapPicker is null)
        {
            return;
        }

        var start = Coordinate;
        var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
        if (await _mapPicker.PickAsync(start?.Latitude, start?.Longitude, owner) is { } picked)
        {
            IsRemove = false;
            SetCoordinates(picked.Latitude, picked.Longitude);
        }
    }

    public void SetCoordinates(double latitude, double longitude)
    {
        LatitudeText = latitude.ToString("0.######", CultureInfo.GetCultureInfo("de-DE"));
        LongitudeText = longitude.ToString("0.######", CultureInfo.GetCultureInfo("de-DE"));
    }

    /// <summary>Ergebnis oder null, wenn die Eingabe ungültig ist.</summary>
    public PlaceDialogResult? BuildResult()
    {
        if (IsRemove)
        {
            return PlaceDialogResult.Clear();
        }

        return Coordinate is { } c ? PlaceDialogResult.Set(c.Latitude, c.Longitude, KeepModifiedTime) : null;
    }

    [GeneratedRegex(@"^\s*(?<a>[-+]?\d+(?:[.,]\d+)?)\s*(?:[;]|,\s+|\s+)\s*(?<b>[-+]?\d+(?:[.,]\d+)?)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex PairRegex();

    [GeneratedRegex(@"^\s*(?<h1>[NSEWOnsewo])?\s*(?<d>\d+(?:[.,]\d+)?)\s*°\s*(?:(?<m>\d+(?:[.,]\d+)?)\s*['′]\s*)?(?:(?<s>\d+(?:[.,]\d+)?)\s*(?:""|″|'')\s*)?(?<h2>[NSEWOnsewo])?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex DmsRegex();

    private static (string Latitude, string Longitude)? SplitPair(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = PairRegex().Match(text);
        return match.Success ? (match.Groups["a"].Value, match.Groups["b"].Value) : null;
    }

    /// <summary>„48,2082“, „48.2082“, „-16.37“, „16,3738 O“, „48°12'29,5"N“; null bei Ungültigem.</summary>
    public static double? TryParseCoordinate(string? text, bool latitude)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var t = text.Trim();
        var dms = DmsRegex().Match(t);
        if (dms.Success)
        {
            var hemisphere = (dms.Groups["h1"].Value + dms.Groups["h2"].Value).ToUpperInvariant();
            var value = Num(dms.Groups["d"].Value) + (Num(dms.Groups["m"].Value) / 60) + (Num(dms.Groups["s"].Value) / 3600);
            if (hemisphere is "S" or "W")
            {
                value = -value;
            }

            return Check(value, latitude);
        }

        var sign = 1.0;
        var last = char.ToUpperInvariant(t[^1]);
        if (last is 'N' or 'S' or 'E' or 'W' or 'O')
        {
            sign = last is 'S' or 'W' ? -1 : 1;
            t = t[..^1].Trim();
        }

        t = t.Replace(',', '.');
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? Check(sign * number, latitude) : null;

        static double Num(string s) => string.IsNullOrEmpty(s) ? 0 : double.Parse(s.Replace(',', '.'), CultureInfo.InvariantCulture);

        static double? Check(double v, bool lat) => double.IsFinite(v) && Math.Abs(v) <= (lat ? 90 : 180) ? v : null;
    }
}
