using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace PhotoViewerV3.Services.Places;

/// <summary>
/// Die eingebetteten GeoNames-Daten im Speicher (ADR-0050): ≈ 70 000 Orte ab 5 000 Einwohnern (cities5000), Länder und
/// Regionen mit deutschen Namen, dazu ein Raster-Index über 1° × 1°-Zellen für <see cref="ReverseGeocoder.Nearest"/>.
/// Format der Ressource siehe <c>build/geonames/Build-GeoNames.ps1</c>. Wird einmal geladen (≈ 0,1–0,2 s) und dann nur
/// gelesen — thread-sicher.
/// </summary>
internal sealed class GeoNamesData
{
    public const string ResourceName = "PhotoViewerV3.Services.Places.geonames.tsv.gz";

    private const int LatCells = 180;
    private const int LonCells = 360;

    private GeoNamesData(
        int[] ids, string[] names, string?[] altNames, double[] latitudes, double[] longitudes, short[] country, int[] region, int[] population,
        string[] countryCodes, string[] countryNames, string[] countryNamesEnglish, string[] regionNames)
    {
        Ids = ids;
        Names = names;
        AltNames = altNames;
        Latitudes = latitudes;
        Longitudes = longitudes;
        Country = country;
        Region = region;
        Population = population;
        CountryCodes = countryCodes;
        CountryNames = countryNames;
        CountryNamesEnglish = countryNamesEnglish;
        RegionNames = regionNames;
        IndexById = new Dictionary<int, int>(ids.Length);
        for (var i = 0; i < ids.Length; i++)
        {
            IndexById[ids[i]] = i;
        }

        (CellStart, CellItems) = BuildGrid(latitudes, longitudes);
    }

    public int[] Ids { get; }

    public string[] Names { get; }

    /// <summary>Weitere Namen (GeoNames-Name, ASCII-Name), mit „|“ getrennt; null ohne.</summary>
    public string?[] AltNames { get; }

    public double[] Latitudes { get; }

    public double[] Longitudes { get; }

    /// <summary>Index in <see cref="CountryCodes"/> je Ort.</summary>
    public short[] Country { get; }

    /// <summary>Index in <see cref="RegionNames"/> je Ort; -1 ohne Region.</summary>
    public int[] Region { get; }

    public int[] Population { get; }

    public string[] CountryCodes { get; }

    public string[] CountryNames { get; }

    public string[] CountryNamesEnglish { get; }

    public string[] RegionNames { get; }

    public Dictionary<int, int> IndexById { get; }

    /// <summary>Raster: Orte der Zelle c stehen in <see cref="CellItems"/>[CellStart[c] .. CellStart[c + 1]).</summary>
    public int[] CellStart { get; }

    public int[] CellItems { get; }

    public int Count => Ids.Length;

    public static int CellOf(double latitude, double longitude)
        => (LatIndex(latitude) * LonCells) + LonIndex(longitude);

    public static int LatIndex(double latitude) => Math.Clamp((int)Math.Floor(latitude + 90), 0, LatCells - 1);

    public static int LonIndex(double longitude) => ((((int)Math.Floor(longitude + 180)) % LonCells) + LonCells) % LonCells;

    public static int Cell(int latIndex, int lonIndex) => (latIndex * LonCells) + (((lonIndex % LonCells) + LonCells) % LonCells);

    public static int LatCellCount => LatCells;

    /// <summary>Lädt die eingebettete Ressource.</summary>
    public static GeoNamesData LoadEmbedded()
    {
        using var stream = typeof(GeoNamesData).Assembly.GetManifestResourceStream(ResourceName)
                           ?? throw new InvalidOperationException($"Ressource {ResourceName} fehlt.");
        return Load(stream);
    }

    /// <summary>Lädt aus einem gzip-komprimierten TSV-Stream (Tests: kleine eigene Daten).</summary>
    public static GeoNamesData Load(Stream gzip)
    {
        using var unzip = new GZipStream(gzip, CompressionMode.Decompress);
        using var reader = new StreamReader(unzip, new UTF8Encoding(false));
        var countryIndex = new Dictionary<string, short>(StringComparer.Ordinal);
        var countryCodes = new List<string>();
        var countryNames = new List<string>();
        var countryEnglish = new List<string>();
        var regionIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var regionNames = new List<string>();
        var ids = new List<int>(72_000);
        var names = new List<string>(72_000);
        var alt = new List<string?>(72_000);
        var lat = new List<double>(72_000);
        var lon = new List<double>(72_000);
        var country = new List<short>(72_000);
        var region = new List<int>(72_000);
        var population = new List<int>(72_000);
        var nameCache = new Dictionary<string, string>(StringComparer.Ordinal);

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length < 2 || line[0] == '#')
            {
                continue;
            }

            var f = line.Split('\t');
            switch (f[0])
            {
                case "C" when f.Length >= 4:
                    countryIndex[f[1]] = (short)countryCodes.Count;
                    countryCodes.Add(f[1]);
                    countryNames.Add(f[2]);
                    countryEnglish.Add(f[3]);
                    break;
                case "R" when f.Length >= 3:
                    regionIndex[f[1]] = regionNames.Count;
                    regionNames.Add(f[2]);
                    break;
                case "P" when f.Length >= 9:
                    if (!countryIndex.TryGetValue(f[6], out var c))
                    {
                        c = (short)countryCodes.Count;
                        countryIndex[f[6]] = c;
                        countryCodes.Add(f[6]);
                        countryNames.Add(f[6]);
                        countryEnglish.Add(f[6]);
                    }

                    ids.Add(int.Parse(f[1], CultureInfo.InvariantCulture));
                    names.Add(Intern(nameCache, f[2]));
                    alt.Add(f[3].Length == 0 ? null : f[3]);
                    lat.Add(double.Parse(f[4], CultureInfo.InvariantCulture));
                    lon.Add(double.Parse(f[5], CultureInfo.InvariantCulture));
                    country.Add(c);
                    region.Add(regionIndex.TryGetValue(f[6] + "." + f[7], out var r) ? r : -1);
                    population.Add((int)Math.Min(int.MaxValue, long.Parse(f[8], CultureInfo.InvariantCulture)));
                    break;
            }
        }

        return new GeoNamesData(
            ids.ToArray(), names.ToArray(), alt.ToArray(), lat.ToArray(), lon.ToArray(), country.ToArray(), region.ToArray(), population.ToArray(),
            countryCodes.ToArray(), countryNames.ToArray(), countryEnglish.ToArray(), regionNames.ToArray());

        static string Intern(Dictionary<string, string> cache, string value)
        {
            if (cache.TryGetValue(value, out var existing))
            {
                return existing;
            }

            cache[value] = value;
            return value;
        }
    }

    private static (int[] Start, int[] Items) BuildGrid(double[] latitudes, double[] longitudes)
    {
        var cells = LatCells * LonCells;
        var start = new int[cells + 1];
        var cellOf = new int[latitudes.Length];
        for (var i = 0; i < latitudes.Length; i++)
        {
            cellOf[i] = CellOf(latitudes[i], longitudes[i]);
            start[cellOf[i] + 1]++;
        }

        for (var c = 0; c < cells; c++)
        {
            start[c + 1] += start[c];
        }

        var items = new int[latitudes.Length];
        var fill = (int[])start.Clone();
        for (var i = 0; i < latitudes.Length; i++)
        {
            items[fill[cellOf[i]]++] = i;
        }

        return (start, items);
    }
}
