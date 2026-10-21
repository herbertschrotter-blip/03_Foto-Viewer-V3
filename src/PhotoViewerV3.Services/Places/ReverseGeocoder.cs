using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PhotoViewerV3.Services.Places;

/// <summary>Ort zu einer Id nachschlagen (Galerie-Filter, Lightbox, Liste) — Ergänzung zu <see cref="IReverseGeocoder"/>.</summary>
public interface IPlaceLookup
{
    /// <summary>Ort zur GeoNames-Id oder null (unbekannte Id).</summary>
    PlaceInfo? GetPlace(int placeId);

    /// <summary>Deutscher Ländername zum ISO-Code (sonst der Code).</summary>
    string CountryName(string countryCode);
}

/// <summary>
/// Ortsnamen offline aus den eingebetteten GeoNames-Daten (ADR-0050). <see cref="Nearest"/> sucht im Raster-Index
/// ringweise um die 1°-Zelle der Koordinate (Abstand als flache Näherung mit cos(Breite), für die Grenze Haversine) —
/// ≈ 1–5 µs je Aufruf (Spike #13); weiter als <see cref="MaxDistanceKm"/> entfernt gilt „kein Ort“. <see cref="Search"/>
/// vergleicht den Text (ohne Akzente, ß = ss) mit Anzeige-, GeoNames- und ASCII-Namen: exakt vor Wortanfang vor
/// Teilstring, dann nach Einwohnern; „Paris, Frankreich“ grenzt über Land oder Region ein. Die Daten werden beim ersten
/// Aufruf geladen (≈ 0,1–0,2 s) — <see cref="Warmup"/> erledigt das im Hintergrund.
/// </summary>
public sealed class ReverseGeocoder : IReverseGeocoder, IPlaceLookup
{
    /// <summary>Weiter entfernt als das: kein Ort (Meer, Wildnis).</summary>
    public const double MaxDistanceKm = 100;

    private const double KmPerDegreeLat = 110.574;
    private const double KmPerDegreeLon = 111.320;
    private const double EarthRadiusKm = 6371.0088;

    private readonly Lazy<GeoNamesData> _data;
    private readonly Lazy<string[]> _searchKeys;
    private readonly ILogger<ReverseGeocoder> _logger;

    public ReverseGeocoder(ILogger<ReverseGeocoder>? logger = null)
        : this(GeoNamesData.LoadEmbedded, logger)
    {
    }

    internal ReverseGeocoder(Func<GeoNamesData> load, ILogger<ReverseGeocoder>? logger = null)
    {
        _logger = logger ?? NullLogger<ReverseGeocoder>.Instance;
        _data = new Lazy<GeoNamesData>(
            () =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var data = load();
                _logger.LogInformation("Ortsnamen (GeoNames) geladen: {Count:N0} Orte in {Ms:N0} ms", data.Count, sw.Elapsed.TotalMilliseconds);
                return data;
            },
            LazyThreadSafetyMode.ExecutionAndPublication);
        _searchKeys = new Lazy<string[]>(BuildSearchKeys, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Zahl der Orte (lädt die Daten).</summary>
    public int Count => _data.Value.Count;

    /// <summary>Daten im Hintergrund laden (App-Start), damit der erste Aufruf nicht wartet.</summary>
    public void Warmup() => _ = Task.Run(() =>
    {
        try
        {
            _ = _data.Value;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ortsnamen (GeoNames) konnten nicht geladen werden");
        }
    });

    public PlaceInfo? Nearest(double latitude, double longitude)
    {
        if (!double.IsFinite(latitude) || !double.IsFinite(longitude) || latitude is < -90 or > 90 || longitude is < -180 or > 180)
        {
            return null;
        }

        var data = _data.Value;
        var best = NearestIndex(data, latitude, longitude, out var distanceKm);
        return best >= 0 && distanceKm <= MaxDistanceKm ? Info(data, best) : null;
    }

    /// <summary>Index des nächsten Orts (-1 ohne) samt Abstand in km.</summary>
    private static int NearestIndex(GeoNamesData data, double latitude, double longitude, out double distanceKm)
    {
        var latIndex = GeoNamesData.LatIndex(latitude);
        var lonIndex = GeoNamesData.LonIndex(longitude);
        var cosLat = Math.Max(0.01, Math.Cos(latitude * Math.PI / 180));
        var best = -1;
        var bestApprox = double.MaxValue;

        // Ring r: alle Zellen mit Chebyshev-Abstand r um die Zelle der Koordinate. Nach Ring r liegt jeder ungeprüfte Ort
        // mindestens r Zellen entfernt — in der Breite r × 110,6 km, in der Länge r × 111,3 km × cos(Breite am Rand).
        for (var r = 0; r <= 60; r++)
        {
            for (var dLat = -r; dLat <= r; dLat++)
            {
                var la = latIndex + dLat;
                if (la < 0 || la >= GeoNamesData.LatCellCount)
                {
                    continue;
                }

                var step = Math.Abs(dLat) == r ? 1 : 2 * r; // innere Zeilen: nur linker und rechter Rand
                for (var dLon = -r; dLon <= r; dLon += Math.Max(1, step))
                {
                    var cell = GeoNamesData.Cell(la, lonIndex + dLon);
                    for (var k = data.CellStart[cell]; k < data.CellStart[cell + 1]; k++)
                    {
                        var i = data.CellItems[k];
                        var dy = (data.Latitudes[i] - latitude) * KmPerDegreeLat;
                        var dx = WrapLon(data.Longitudes[i] - longitude) * KmPerDegreeLon * cosLat;
                        var d2 = (dx * dx) + (dy * dy);
                        if (d2 < bestApprox)
                        {
                            bestApprox = d2;
                            best = i;
                        }
                    }

                    if (r == 0)
                    {
                        break;
                    }
                }
            }

            var edgeLat = Math.Min(89.9, Math.Abs(latitude) + r + 1);
            var minRingKm = r * Math.Min(KmPerDegreeLat, KmPerDegreeLon * Math.Cos(edgeLat * Math.PI / 180));
            if ((best >= 0 && Math.Sqrt(bestApprox) <= minRingKm) || minRingKm > MaxDistanceKm * 1.5)
            {
                break;
            }
        }

        distanceKm = best >= 0 ? HaversineKm(latitude, longitude, data.Latitudes[best], data.Longitudes[best]) : double.MaxValue;
        return best;
    }

    private static double WrapLon(double delta) => delta > 180 ? delta - 360 : delta < -180 ? delta + 360 : delta;

    /// <summary>Großkreis-Abstand in km.</summary>
    public static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
    {
        var p1 = lat1 * Math.PI / 180;
        var p2 = lat2 * Math.PI / 180;
        var dp = p2 - p1;
        var dl = (lon2 - lon1) * Math.PI / 180;
        var a = (Math.Sin(dp / 2) * Math.Sin(dp / 2)) + (Math.Cos(p1) * Math.Cos(p2) * Math.Sin(dl / 2) * Math.Sin(dl / 2));
        return 2 * EarthRadiusKm * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }

    public IReadOnlyList<(PlaceInfo Place, double Latitude, double Longitude)> Search(string text, int max)
    {
        if (string.IsNullOrWhiteSpace(text) || max <= 0)
        {
            return [];
        }

        var data = _data.Value;
        var keys = _searchKeys.Value;
        var parts = text.Split(',', 2);
        var needle = Normalize(parts[0]);
        var scope = parts.Length > 1 ? Normalize(parts[1]) : string.Empty;
        if (needle.Length == 0)
        {
            return [];
        }

        var hits = new List<(int Index, int Rank)>();
        for (var i = 0; i < keys.Length; i++)
        {
            var rank = Rank(keys[i], needle);
            if (rank < 0)
            {
                continue;
            }

            if (scope.Length > 0 && !MatchesScope(data, i, scope))
            {
                continue;
            }

            hits.Add((i, rank));
        }

        return hits
            .OrderBy(h => h.Rank)
            .ThenByDescending(h => data.Population[h.Index])
            .ThenBy(h => data.Ids[h.Index])
            .Take(max)
            .Select(h => (Info(data, h.Index), data.Latitudes[h.Index], data.Longitudes[h.Index]))
            .ToList();
    }

    /// <summary>0 = ein Name genau, 1 = ein Name beginnt so, 2 = ein Wort beginnt so, 3 = enthält; -1 = kein Treffer.</summary>
    private static int Rank(string key, string needle)
    {
        var best = -1;
        foreach (var name in key.Split('|'))
        {
            int rank;
            if (name == needle)
            {
                rank = 0;
            }
            else if (name.StartsWith(needle, StringComparison.Ordinal))
            {
                rank = 1;
            }
            else
            {
                var at = name.IndexOf(needle, StringComparison.Ordinal);
                if (at < 0)
                {
                    continue;
                }

                rank = name[at - 1] is ' ' or '-' or '\'' ? 2 : 3;
            }

            if (best < 0 || rank < best)
            {
                best = rank;
            }
        }

        return best;
    }

    private static bool MatchesScope(GeoNamesData data, int index, string scope)
    {
        var country = data.Country[index];
        if (Normalize(data.CountryNames[country]).StartsWith(scope, StringComparison.Ordinal)
            || Normalize(data.CountryNamesEnglish[country]).StartsWith(scope, StringComparison.Ordinal)
            || string.Equals(data.CountryCodes[country], scope, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var region = data.Region[index];
        return region >= 0 && Normalize(data.RegionNames[region]).StartsWith(scope, StringComparison.Ordinal);
    }

    private string[] BuildSearchKeys()
    {
        var data = _data.Value;
        var keys = new string[data.Count];
        for (var i = 0; i < data.Count; i++)
        {
            var key = data.AltNames[i] is { } alt ? Normalize(data.Names[i]) + "|" + Normalize(alt) : Normalize(data.Names[i]);
            if (data.Names[i].AsSpan().IndexOfAny("äöüÄÖÜ") >= 0)
            {
                key += "|" + Normalize(ExpandUmlauts(data.Names[i])); // „Muenchen“ findet „München“
            }

            keys[i] = key;
        }

        return keys;
    }

    private static string ExpandUmlauts(string text)
        => text.Replace("ä", "ae", StringComparison.Ordinal).Replace("ö", "oe", StringComparison.Ordinal).Replace("ü", "ue", StringComparison.Ordinal)
            .Replace("Ä", "Ae", StringComparison.Ordinal).Replace("Ö", "Oe", StringComparison.Ordinal).Replace("Ü", "Ue", StringComparison.Ordinal);

    /// <summary>Kleinbuchstaben ohne Akzente (é → e, ü → u), ß → ss, Leerraum vereinheitlicht.</summary>
    internal static string Normalize(string text)
    {
        var decomposed = text.Trim().ToLowerInvariant().Replace("ß", "ss", StringComparison.Ordinal).Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        var space = false;
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                if (!space && sb.Length > 0)
                {
                    sb.Append(' ');
                }

                space = true;
                continue;
            }

            space = false;
            sb.Append(ch);
        }

        return sb.ToString().TrimEnd();
    }

    public PlaceInfo? GetPlace(int placeId)
    {
        var data = _data.Value;
        return data.IndexById.TryGetValue(placeId, out var i) ? Info(data, i) : null;
    }

    public string CountryName(string countryCode)
    {
        var data = _data.Value;
        var i = Array.IndexOf(data.CountryCodes, countryCode);
        return i >= 0 ? data.CountryNames[i] : countryCode;
    }

    private static PlaceInfo Info(GeoNamesData data, int i)
    {
        var country = data.Country[i];
        var region = data.Region[i];
        return new PlaceInfo(data.Ids[i], data.Names[i], region >= 0 ? data.RegionNames[region] : null, data.CountryCodes[country], data.CountryNames[country]);
    }
}
