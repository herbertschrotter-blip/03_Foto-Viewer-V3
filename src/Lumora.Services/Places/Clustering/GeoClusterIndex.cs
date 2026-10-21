namespace Lumora.Services.Places.Clustering;

/// <summary>Ein verortetes Foto für das Clustering: Position plus Rangfolge für das „beste“ Foto einer Gruppe.</summary>
/// <param name="Rating">Bewertung 0–5 (0 = keine).</param>
/// <param name="SortKey">Je größer, desto neuer (z. B. Aufnahmedatum als Unix-Sekunden, sonst Änderungsdatum).</param>
public readonly record struct ClusterInput(int FileId, double Latitude, double Longitude, byte Rating, long SortKey);

/// <summary>Einstellungen des Clusterings.</summary>
public sealed record GeoClusterOptions
{
    /// <summary>Gröbste Zoomstufe (0 = ganze Welt auf einer Kachel).</summary>
    public int MinZoom { get; init; }

    /// <summary>Feinste Zoomstufe (OSM: 19). Gruppen, die hier noch beisammen sind, zerfallen nie (gleicher Ort).</summary>
    public int MaxZoom { get; init; } = 19;

    /// <summary>Kantenlänge einer Rasterzelle in Bildschirm-Pixeln (Zweierpotenz-Bruchteil einer 256-px-Kachel: 32, 64, 128).</summary>
    public int CellPixels { get; init; } = 64;

    /// <summary>Marker, deren Mittelpunkte näher als das liegen, werden zu einem zusammengelegt (gegen Überlappen an Zellgrenzen).</summary>
    public double MergePixels { get; init; } = 56;
}

/// <summary>
/// Ein Marker der Karte: Gruppe (<see cref="Count"/> &gt; 1) oder ein Foto, Position in Einheitskoordinaten
/// (<see cref="MercatorProjection"/>) = Schwerpunkt seiner Fotos. Gehört zu einem <see cref="GeoClusterIndex"/>.
/// </summary>
public sealed class GeoCluster
{
    internal GeoCluster(int zoom, double x, double y, int count, int bestFileId, (int Start, int End)[] spotRanges)
    {
        Zoom = zoom;
        X = x;
        Y = y;
        Count = count;
        BestFileId = bestFileId;
        SpotRanges = spotRanges;
    }

    /// <summary>Zoomstufe, für die der Marker berechnet wurde.</summary>
    public int Zoom { get; }

    public double X { get; }

    public double Y { get; }

    public int Count { get; }

    /// <summary>Vorschaubild: höchste Bewertung, sonst neuestes Foto.</summary>
    public int BestFileId { get; }

    public bool IsSinglePhoto => Count == 1;

    public double Latitude => MercatorProjection.YToLatitude(Y);

    public double Longitude => MercatorProjection.XToLongitude(X);

    /// <summary>Aufnahmeorte des Markers als Bereiche der Z-Reihenfolge (zusammenhängend, nicht überlappend).</summary>
    internal (int Start, int End)[] SpotRanges { get; }
}

/// <summary>
/// Gruppen verorteter Fotos für die Orte-Karte (ADR-0051) als hierarchisches Raster: Die Fotos werden einmal nach ihrem
/// Z-Wert (Morton-Code, 2 × 30 Bit ≈ 4 cm) sortiert; gleiche Codes sind ein Aufnahmeort. Eine Rasterzelle der Zoomstufe
/// z (Kante <see cref="GeoClusterOptions.CellPixels"/> Pixel) ist ein Präfix dieses Codes und damit ein zusammenhängender
/// Bereich der Sortierung — jede Zelle zerfällt beim Hineinzoomen in höchstens vier Teilzellen, Gruppen springen nie um.
/// Vorberechnet werden nur Präfixsummen (Anzahl, Schwerpunkt) und ein Segmentbaum für das beste Foto: eine Zelle kostet
/// zwei binäre Suchen, eine Ausschnitt-Abfrage ein paar hundert Zellen. Danach werden Marker, die näher als
/// <see cref="GeoClusterOptions.MergePixels"/> beieinander liegen, zusammengelegt (gierig, größte zuerst) — sonst
/// überlappten Gruppen links und rechts einer Zellgrenze. Unveränderlich und damit thread-sicher lesbar.
/// <para>Speicher ≈ 45 Byte je Foto; Aufbau = eine Sortierung (500 000 Fotos: siehe ADR-0051).</para>
/// </summary>
public sealed class GeoClusterIndex
{
    private const int Bits = 30;
    private const double Quant = 1 << Bits;

    private readonly ulong[] _spotCode;
    private readonly int[] _spotPhotoStart; // Länge Spots + 1
    private readonly double[] _cumX; // Σ x·Anzahl bis Spot i (Länge Spots + 1)
    private readonly double[] _cumY;
    private readonly int[] _bestTree; // Segmentbaum über Spots: Foto-Position des besten Fotos
    private readonly int[] _photoFileId; // Fotos in Z-Reihenfolge
    private readonly double[] _photoX;
    private readonly double[] _photoY;
    private readonly byte[] _photoRating;
    private readonly long[] _photoSortKey;
    private readonly int _cellShift; // log2(256 / CellPixels)
    private readonly int _maxMergeCells; // so viele Zellen kann eine Zusammenlege-Scheibe höchstens berühren

    private GeoClusterIndex(GeoClusterOptions options, ulong[] spotCode, int[] spotPhotoStart, double[] cumX, double[] cumY, int[] bestTree,
        int[] photoFileId, double[] photoX, double[] photoY, byte[] photoRating, long[] photoSortKey)
    {
        Options = options;
        _spotCode = spotCode;
        _spotPhotoStart = spotPhotoStart;
        _cumX = cumX;
        _cumY = cumY;
        _bestTree = bestTree;
        _photoFileId = photoFileId;
        _photoX = photoX;
        _photoY = photoY;
        _photoRating = photoRating;
        _photoSortKey = photoSortKey;
        _cellShift = System.Numerics.BitOperations.Log2((uint)(MercatorProjection.TileSize / options.CellPixels));
        var perAxis = (int)Math.Ceiling(2 * options.MergePixels / options.CellPixels) + 1;
        _maxMergeCells = perAxis * perAxis;
    }

    public GeoClusterOptions Options { get; }

    public int MinZoom => Options.MinZoom;

    public int MaxZoom => Options.MaxZoom;

    public int PhotoCount => _photoFileId.Length;

    /// <summary>Anzahl verschiedener Aufnahmeorte (gleiche Koordinaten zusammengelegt).</summary>
    public int SpotCount => _spotCode.Length;

    /// <summary>Leerer Index (keine verorteten Fotos).</summary>
    public static GeoClusterIndex Empty { get; } = Build([]);

    public static GeoClusterIndex Build(IReadOnlyList<ClusterInput> points, GeoClusterOptions? options = null)
    {
        options ??= new GeoClusterOptions();
        if (options.MinZoom < 0 || options.MaxZoom < options.MinZoom || options.CellPixels is < 4 or > 256
            || !System.Numerics.BitOperations.IsPow2(options.CellPixels) || options.MergePixels < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "0 ≤ MinZoom ≤ MaxZoom, CellPixels Zweierpotenz 4…256, MergePixels ≥ 0");
        }

        var shift = System.Numerics.BitOperations.Log2((uint)(MercatorProjection.TileSize / options.CellPixels));
        if (options.MaxZoom + shift > Bits)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxZoom zu groß für die Rasterauflösung");
        }

        var n = points.Count;
        var codes = new ulong[n];
        var order = new int[n];
        var xs = new double[n];
        var ys = new double[n];
        for (var i = 0; i < n; i++)
        {
            xs[i] = MercatorProjection.LongitudeToX(points[i].Longitude);
            ys[i] = MercatorProjection.LatitudeToY(points[i].Latitude);
            codes[i] = Morton(Quantize(xs[i]), Quantize(ys[i]));
            order[i] = i;
        }

        Array.Sort(codes, order);

        var photoFileId = new int[n];
        var photoX = new double[n];
        var photoY = new double[n];
        var photoRating = new byte[n];
        var photoSortKey = new long[n];
        for (var pos = 0; pos < n; pos++)
        {
            var p = points[order[pos]];
            photoFileId[pos] = p.FileId;
            photoX[pos] = xs[order[pos]];
            photoY[pos] = ys[order[pos]];
            photoRating[pos] = p.Rating;
            photoSortKey[pos] = p.SortKey;
        }

        // Aufnahmeorte = Läufe gleicher Codes
        var spotCount = 0;
        for (var i = 0; i < n; i++)
        {
            if (i == 0 || codes[i] != codes[i - 1])
            {
                spotCount++;
            }
        }

        var spotCode = new ulong[spotCount];
        var spotStart = new int[spotCount + 1];
        var cumX = new double[spotCount + 1];
        var cumY = new double[spotCount + 1];
        var spotBest = new int[spotCount];
        var s = -1;
        for (var i = 0; i < n; i++)
        {
            if (i == 0 || codes[i] != codes[i - 1])
            {
                s++;
                spotCode[s] = codes[i];
                spotStart[s] = i;
                spotBest[s] = i;
                cumX[s + 1] = cumX[s];
                cumY[s + 1] = cumY[s];
            }
            else if (Better(photoRating, photoSortKey, photoFileId, i, spotBest[s]))
            {
                spotBest[s] = i;
            }

            cumX[s + 1] += photoX[i];
            cumY[s + 1] += photoY[i];
        }

        spotStart[spotCount] = n;

        // Segmentbaum (iterativ, Blätter ab spotCount)
        var tree = new int[Math.Max(2, 2 * spotCount)];
        for (var i = 0; i < spotCount; i++)
        {
            tree[spotCount + i] = spotBest[i];
        }

        for (var i = spotCount - 1; i >= 1; i--)
        {
            var a = tree[2 * i];
            var b = tree[(2 * i) + 1];
            tree[i] = Better(photoRating, photoSortKey, photoFileId, b, a) ? b : a;
        }

        return new GeoClusterIndex(options, spotCode, spotStart, cumX, cumY, tree, photoFileId, photoX, photoY, photoRating, photoSortKey);
    }

    /// <summary>
    /// Marker der Zoomstufe <paramref name="zoom"/> (auf MinZoom…MaxZoom begrenzt) für das Rechteck in
    /// Einheitskoordinaten: alle belegten Zellen, die es berühren, danach zu nahe Marker zusammengelegt.
    /// </summary>
    public List<GeoCluster> Query(double minX, double minY, double maxX, double maxY, int zoom)
    {
        var z = Math.Clamp(zoom, MinZoom, MaxZoom);
        var raw = RawCells(minX, minY, maxX, maxY, z);
        return Merge(raw, z);
    }

    /// <summary>Marker im geografischen Ausschnitt (Süd/West/Nord/Ost in Grad).</summary>
    public List<GeoCluster> Query(GeoBounds bounds, int zoom)
        => Query(
            MercatorProjection.LongitudeToX(bounds.West),
            MercatorProjection.LatitudeToY(bounds.North),
            MercatorProjection.LongitudeToX(bounds.East),
            MercatorProjection.LatitudeToY(bounds.South),
            zoom);

    /// <summary>
    /// Kleinste Zoomstufe, auf der die Gruppe in mindestens zwei Marker zerfällt; <see cref="int.MaxValue"/>, wenn sie
    /// bis <see cref="MaxZoom"/> beisammen bleibt (alle Fotos am selben Ort) oder ein einzelnes Foto ist.
    /// </summary>
    public int GetExpansionZoom(GeoCluster cluster)
    {
        if (cluster.Count <= 1)
        {
            return int.MaxValue;
        }

        for (var z = cluster.Zoom + 1; z <= MaxZoom; z++)
        {
            var shift = PrefixShift(z);
            var raw = new List<Raw>();
            foreach (var (start, end) in cluster.SpotRanges)
            {
                for (var a = start; a < end;)
                {
                    var prefix = _spotCode[a] >> shift;
                    var b = Math.Min(end, LowerBound((prefix + 1) << shift, a, end));
                    raw.Add(MakeRaw(a, b));
                    a = b;
                }
            }

            // Eine Zusammenlege-Scheibe (56 px) berührt höchstens 3 × 3 Zellen (64 px) — mehr Zellen ergeben sicher ≥ 2 Marker
            if (raw.Count > _maxMergeCells || Merge(raw, z).Count >= 2)
            {
                return z;
            }
        }

        return int.MaxValue;
    }

    /// <summary>Alle Dateien des Markers (in Z-Reihenfolge).</summary>
    public IReadOnlyList<int> GetFileIds(GeoCluster cluster)
    {
        var result = new int[cluster.Count];
        var at = 0;
        foreach (var (start, end) in cluster.SpotRanges)
        {
            var from = _spotPhotoStart[start];
            var length = _spotPhotoStart[end] - from;
            Array.Copy(_photoFileId, from, result, at, length);
            at += length;
        }

        return result;
    }

    /// <summary>Begrenzungsrahmen der Fotos des Markers.</summary>
    public GeoBounds GetBounds(GeoCluster cluster)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var (start, end) in cluster.SpotRanges)
        {
            for (var s = start; s < end; s++)
            {
                var p = _spotPhotoStart[s]; // alle Fotos eines Ortes liegen (auf 4 cm) gleich
                minX = Math.Min(minX, _photoX[p]);
                maxX = Math.Max(maxX, _photoX[p]);
                minY = Math.Min(minY, _photoY[p]);
                maxY = Math.Max(maxY, _photoY[p]);
            }
        }

        return new GeoBounds(
            MercatorProjection.YToLatitude(maxY),
            MercatorProjection.XToLongitude(minX),
            MercatorProjection.YToLatitude(minY),
            MercatorProjection.XToLongitude(maxX));
    }

    /// <summary>Begrenzungsrahmen aller Fotos oder null.</summary>
    public GeoBounds? GetExtent()
        => SpotCount == 0 ? null : GetBounds(new GeoCluster(MinZoom, 0, 0, PhotoCount, 0, [(0, SpotCount)]));

    // ---- Raster ------------------------------------------------------------------------------------------------------

    /// <summary>Belegte Zelle: Spot-Bereich, Anzahl, Schwerpunkt, bestes Foto.</summary>
    private readonly record struct Raw(int Start, int End, int Count, double X, double Y, int Best);

    private Raw MakeRaw(int a, int b)
    {
        var count = _spotPhotoStart[b] - _spotPhotoStart[a];
        return new Raw(a, b, count, (_cumX[b] - _cumX[a]) / count, (_cumY[b] - _cumY[a]) / count, BestInRange(a, b));
    }

    private List<Raw> RawCells(double minX, double minY, double maxX, double maxY, int z)
    {
        var raw = new List<Raw>();
        if (SpotCount == 0)
        {
            return raw;
        }

        var level = z + _cellShift; // Bits je Achse der Zelle
        var cells = 1L << level;
        var cx0 = Math.Clamp((long)Math.Floor(Math.Max(0, minX) * cells), 0, cells - 1);
        var cx1 = Math.Clamp((long)Math.Floor(Math.Min(1, maxX) * cells), 0, cells - 1);
        var cy0 = Math.Clamp((long)Math.Floor(Math.Max(0, minY) * cells), 0, cells - 1);
        var cy1 = Math.Clamp((long)Math.Floor(Math.Min(1, maxY) * cells), 0, cells - 1);
        if (maxX < 0 || maxY < 0 || minX > 1 || minY > 1)
        {
            return raw;
        }

        var shift = PrefixShift(z);
        var cellCount = (cx1 - cx0 + 1) * (cy1 - cy0 + 1);
        if (cellCount > SpotCount)
        {
            // Großes Rechteck (z. B. ganze Welt bei hohem Zoom): alle Orte der Reihe nach, nach Zellen gruppiert
            for (var a = 0; a < SpotCount;)
            {
                var prefix = _spotCode[a] >> shift;
                var b = LowerBound((prefix + 1) << shift, a, SpotCount);
                var (cx, cy) = Unmorton(prefix);
                if (cx >= cx0 && cx <= cx1 && cy >= cy0 && cy <= cy1)
                {
                    raw.Add(MakeRaw(a, b));
                }

                a = b;
            }

            return raw;
        }

        for (var cy = cy0; cy <= cy1; cy++)
        {
            for (var cx = cx0; cx <= cx1; cx++)
            {
                var prefix = Morton((uint)cx, (uint)cy);
                var a = LowerBound(prefix << shift, 0, SpotCount);
                if (a >= SpotCount || _spotCode[a] >> shift != prefix)
                {
                    continue;
                }

                var b = LowerBound((prefix + 1) << shift, a, SpotCount);
                raw.Add(MakeRaw(a, b));
            }
        }

        return raw;
    }

    /// <summary>
    /// Zu nahe Marker zusammenlegen: größte zuerst, jede nimmt die noch freien im Abstand auf. Nachbarn über ein Raster
    /// mit Zellgröße = Abstand (3 × 3 Zellen) — auch bei sehr vielen Zellen (ganze Welt, hoher Zoom) linear.
    /// </summary>
    private List<GeoCluster> Merge(List<Raw> raw, int z)
    {
        var result = new List<GeoCluster>(raw.Count);
        if (raw.Count == 0)
        {
            return result;
        }

        raw.Sort((p, q) => q.Count != p.Count ? q.Count.CompareTo(p.Count) : p.Start.CompareTo(q.Start));
        var distance = Options.MergePixels / MercatorProjection.WorldSize(z);
        var d2 = distance * distance;
        var used = new bool[raw.Count];
        Dictionary<long, List<int>>? grid = null;
        if (raw.Count > 32 && distance > 0)
        {
            grid = new Dictionary<long, List<int>>(raw.Count);
            for (var i = 0; i < raw.Count; i++)
            {
                var key = GridKey(raw[i].X, raw[i].Y, distance, 0, 0);
                if (!grid.TryGetValue(key, out var list))
                {
                    grid[key] = list = new List<int>(2);
                }

                list.Add(i);
            }
        }

        var members = new List<int>();
        for (var i = 0; i < raw.Count; i++)
        {
            if (used[i])
            {
                continue;
            }

            used[i] = true;
            var seed = raw[i];
            members.Clear();
            members.Add(i);
            if (grid is null)
            {
                for (var j = i + 1; j < raw.Count; j++)
                {
                    TryAbsorb(j);
                }
            }
            else
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    for (var dy = -1; dy <= 1; dy++)
                    {
                        if (grid.TryGetValue(GridKey(seed.X, seed.Y, distance, dx, dy), out var list))
                        {
                            foreach (var j in list)
                            {
                                TryAbsorb(j);
                            }
                        }
                    }
                }
            }

            if (members.Count == 1)
            {
                result.Add(new GeoCluster(z, seed.X, seed.Y, seed.Count, _photoFileId[seed.Best], [(seed.Start, seed.End)]));
                continue;
            }

            var count = 0;
            double sx = 0, sy = 0;
            var best = seed.Best;
            var ranges = new (int, int)[members.Count];
            for (var k = 0; k < members.Count; k++)
            {
                var m = raw[members[k]];
                count += m.Count;
                sx += m.X * m.Count;
                sy += m.Y * m.Count;
                if (Better(_photoRating, _photoSortKey, _photoFileId, m.Best, best))
                {
                    best = m.Best;
                }

                ranges[k] = (m.Start, m.End);
            }

            Array.Sort(ranges);
            result.Add(new GeoCluster(z, sx / count, sy / count, count, _photoFileId[best], ranges));

            void TryAbsorb(int j)
            {
                if (used[j])
                {
                    return;
                }

                var ddx = raw[j].X - seed.X;
                var ddy = raw[j].Y - seed.Y;
                if ((ddx * ddx) + (ddy * ddy) <= d2)
                {
                    used[j] = true;
                    members.Add(j);
                }
            }
        }

        return result;
    }

    private static long GridKey(double x, double y, double size, int dx, int dy)
        => (((long)Math.Floor(x / size) + dx) << 32) ^ (((long)Math.Floor(y / size) + dy) & 0xFFFFFFFFL);

    private int BestInRange(int from, int to)
    {
        var n = SpotCount;
        int? best = null;
        for (int l = from + n, r = to + n; l < r; l >>= 1, r >>= 1)
        {
            if ((l & 1) == 1)
            {
                best = Pick(best, _bestTree[l++]);
            }

            if ((r & 1) == 1)
            {
                best = Pick(best, _bestTree[--r]);
            }
        }

        return best ?? _spotPhotoStart[from];
    }

    private int Pick(int? current, int candidate)
        => current is not { } c || Better(_photoRating, _photoSortKey, _photoFileId, candidate, c) ? candidate : c;

    private int PrefixShift(int z) => 2 * (Bits - (z + _cellShift));

    private int LowerBound(ulong value, int lo, int hi)
    {
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (_spotCode[mid] < value)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    /// <summary>Höhere Bewertung, sonst neuer, sonst größere FileId (stabil).</summary>
    private static bool Better(byte[] rating, long[] sortKey, int[] fileId, int a, int b)
    {
        if (rating[a] != rating[b])
        {
            return rating[a] > rating[b];
        }

        if (sortKey[a] != sortKey[b])
        {
            return sortKey[a] > sortKey[b];
        }

        return fileId[a] > fileId[b];
    }

    private static uint Quantize(double unit) => (uint)Math.Clamp((long)(unit * Quant), 0, (1L << Bits) - 1);

    /// <summary>Z-Wert: Bits von x und y verschränkt (y höherwertig).</summary>
    private static ulong Morton(uint x, uint y) => Spread(x) | (Spread(y) << 1);

    private static (long X, long Y) Unmorton(ulong code) => (Compact(code), Compact(code >> 1));

    private static ulong Spread(uint value)
    {
        ulong x = value;
        x = (x | (x << 16)) & 0x0000FFFF0000FFFFUL;
        x = (x | (x << 8)) & 0x00FF00FF00FF00FFUL;
        x = (x | (x << 4)) & 0x0F0F0F0F0F0F0F0FUL;
        x = (x | (x << 2)) & 0x3333333333333333UL;
        x = (x | (x << 1)) & 0x5555555555555555UL;
        return x;
    }

    private static long Compact(ulong code)
    {
        var x = code & 0x5555555555555555UL;
        x = (x | (x >> 1)) & 0x3333333333333333UL;
        x = (x | (x >> 2)) & 0x0F0F0F0F0F0F0F0FUL;
        x = (x | (x >> 4)) & 0x00FF00FF00FF00FFUL;
        x = (x | (x >> 8)) & 0x0000FFFF0000FFFFUL;
        x = (x | (x >> 16)) & 0x00000000FFFFFFFFUL;
        return (long)x;
    }
}
