using System;
using System.Windows;

namespace PhotoViewerV3.Gallery.Controls;

/// <summary>
/// Item mit eigenem Seitenverhältnis (Breite / Höhe) für die Collage (<see cref="JustifiedTilePanel"/>). Items ohne
/// dieses Interface oder mit ungültigem Wert gelten als 4:3.
/// </summary>
public interface IJustifiedLayoutItem
{
    /// <summary>Seitenverhältnis Breite / Höhe des angezeigten Bilds (&gt; 0).</summary>
    double LayoutAspectRatio { get; }
}

/// <summary>
/// Reihen-Umbruch der Collage (ADR-0044, „Justified Layout“ wie Google Fotos/Flickr, Mockup 06): Items in Seitenreihenfolge
/// füllen eine Reihe bei Zielhöhe, bis die Breite überläuft; dann wird die Reihe so weit verkleinert, dass sie genau die
/// verfügbare Breite füllt (randvoll, Bildhöhe ≤ Zielhöhe — es wird also nie über die Zielhöhe gestreckt und das
/// Vorschaubild muss nie größer dekodiert werden als für die Zielhöhe). Die letzte Reihe bleibt bei Zielhöhe und
/// linksbündig (nicht gestreckt). Ein Bild, das schon allein breiter als die Reihe wäre (Panorama), bekommt eine eigene
/// Reihe mit voller Breite und entsprechend kleinerer Höhe.
/// <para>
/// Jede Zelle ist Bild + <see cref="Spacing"/> breit und hoch (der Container-Rand liegt in der Zelle), damit das Bild
/// in der Zelle exakt sein Seitenverhältnis behält. Seitenverhältnisse werden auf
/// [<see cref="MinAspectRatio"/>, <see cref="MaxAspectRatio"/>] begrenzt: sehr hohe Bilder würden sonst zu
/// schmalen Streifen, sehr breite zu dünnen Bändern — nur diese werden in der Zelle beschnitten (UniformToFill).
/// </para>
/// <para>
/// Laufzeit O(n), Arrays werden über Durchläufe wiederverwendet (1000 Items: Messung in ADR-0044). Zellkanten liegen auf
/// ganzen DIP (keine Unschärfe an den Fugen); die rechte Kante einer gefüllten Reihe ist exakt die verfügbare Breite.
/// </para>
/// </summary>
public sealed class JustifiedRowLayout
{
    /// <summary>Seitenverhältnis für Items ohne bekannte Maße (Archive, Dateien vor dem ersten Vorschaubild).</summary>
    public const double DefaultAspectRatio = 4.0 / 3.0;

    /// <summary>Untergrenze (hohe Bilder): Bildschirmfotos im Hochformat (≈ 0,46) bleiben unbeschnitten.</summary>
    public const double MinAspectRatio = 0.4;

    /// <summary>Obergrenze (Panoramen): breitere werden in der Zelle mittig beschnitten.</summary>
    public const double MaxAspectRatio = 6.0;

    private double[] _aspect = [];
    private double[] _itemX = [];
    private double[] _itemWidth = [];
    private int[] _itemRow = [];
    private double[] _rowTop = [];
    private double[] _rowHeight = [];
    private int[] _rowFirst = [];

    /// <summary>Anzahl der Items im letzten <see cref="Compute"/>.</summary>
    public int ItemCount { get; private set; }

    /// <summary>Anzahl der Reihen.</summary>
    public int RowCount { get; private set; }

    /// <summary>Verfügbare Breite in DIP, für die berechnet wurde.</summary>
    public double Width { get; private set; }

    /// <summary>Ziel-Bildhöhe in DIP (ohne <see cref="Spacing"/>).</summary>
    public double TargetRowHeight { get; private set; }

    /// <summary>Zusätzliche Breite und Höhe je Zelle (Rand um das Bild, ergibt die Fugen).</summary>
    public double Spacing { get; private set; }

    /// <summary>Gesamthöhe aller Reihen.</summary>
    public double ExtentHeight { get; private set; }

    /// <summary>Begrenzt ein Seitenverhältnis auf den zulässigen Bereich; ungültige Werte → 4:3.</summary>
    public static double ClampAspect(double aspect)
        => double.IsFinite(aspect) && aspect > 0 ? Math.Clamp(aspect, MinAspectRatio, MaxAspectRatio) : DefaultAspectRatio;

    /// <summary>
    /// Berechnet alle Reihen für die Seitenverhältnisse <paramref name="aspects"/> (Breite / Höhe, Seitenreihenfolge)
    /// bei <paramref name="width"/> DIP Breite, Ziel-Bildhöhe <paramref name="targetRowHeight"/> und Zellrand
    /// <paramref name="spacing"/>.
    /// </summary>
    public void Compute(ReadOnlySpan<double> aspects, double width, double targetRowHeight, double spacing)
    {
        var n = aspects.Length;
        EnsureCapacity(n);
        spacing = double.IsFinite(spacing) && spacing > 0 ? spacing : 0;
        targetRowHeight = double.IsFinite(targetRowHeight) && targetRowHeight >= 1 ? targetRowHeight : 1;
        width = double.IsFinite(width) ? Math.Max(width, spacing + 1) : spacing + 1;

        ItemCount = n;
        Width = width;
        TargetRowHeight = targetRowHeight;
        Spacing = spacing;
        RowCount = 0;

        var y = 0.0;
        var start = 0;
        var sum = 0.0;
        for (var i = 0; i < n; i++)
        {
            var a = ClampAspect(aspects[i]);
            _aspect[i] = a;
            sum += a;
            var count = i - start + 1;
            if (sum * targetRowHeight + count * spacing >= width)
            {
                // Überlauf: Reihe genau auf die Breite verkleinern (Bildhöhe ≤ Zielhöhe).
                var height = Math.Max(1.0, (width - count * spacing) / sum);
                y = CloseRow(start, i, height, y, justify: true);
                start = i + 1;
                sum = 0;
            }
        }

        LastRowFilled = start >= n && n > 0;
        if (start < n)
        {
            y = CloseRow(start, n - 1, targetRowHeight, y, justify: false); // letzte Reihe: Zielhöhe, linksbündig
        }

        ExtentHeight = y;
    }

    /// <summary>true, wenn auch die letzte Reihe randvoll ist (ihr letztes Item hat sie genau gefüllt).</summary>
    public bool LastRowFilled { get; private set; }

    /// <summary>true für randvolle Reihen: alle außer der letzten, diese nur bei <see cref="LastRowFilled"/>.</summary>
    public bool IsFilledRow(int row) => row < RowCount - 1 || (row == RowCount - 1 && LastRowFilled);

    /// <summary>Reihe des Items <paramref name="index"/>.</summary>
    public int RowOf(int index) => _itemRow[index];

    /// <summary>Erstes Item der Reihe <paramref name="row"/>.</summary>
    public int RowFirst(int row) => _rowFirst[row];

    /// <summary>Letztes Item der Reihe <paramref name="row"/> (inklusive).</summary>
    public int RowLast(int row) => row + 1 < RowCount ? _rowFirst[row + 1] - 1 : ItemCount - 1;

    /// <summary>Oberkante der Reihe <paramref name="row"/>.</summary>
    public double RowTop(int row) => _rowTop[row];

    /// <summary>Höhe der Zellen in Reihe <paramref name="row"/> (Bild + Rand).</summary>
    public double RowHeight(int row) => _rowHeight[row];

    /// <summary>Begrenztes Seitenverhältnis, mit dem das Item angeordnet wurde.</summary>
    public double AspectOf(int index) => _aspect[index];

    /// <summary>Zelle des Items <paramref name="index"/> (unverschoben, Inhalt-Koordinaten).</summary>
    public Rect ItemRect(int index)
    {
        var row = _itemRow[index];
        return new Rect(_itemX[index], _rowTop[row], _itemWidth[index], _rowHeight[row]);
    }

    /// <summary>Reihe, in der die Höhe <paramref name="y"/> liegt (binäre Suche; vor der ersten → 0, nach der letzten → letzte); -1 ohne Reihen.</summary>
    public int RowAt(double y)
    {
        if (RowCount == 0)
        {
            return -1;
        }

        int lo = 0, hi = RowCount - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) >> 1;
            if (_rowTop[mid] <= y)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return lo;
    }

    private double CloseRow(int first, int last, double imageHeight, double top, bool justify)
    {
        var row = RowCount++;
        var cellHeight = Math.Round(imageHeight) + Spacing;
        _rowTop[row] = top;
        _rowHeight[row] = cellHeight;
        _rowFirst[row] = first;

        // Kanten aus der aufsummierten Breite runden (ganze DIP, kein Fehler-Aufsummieren); gefüllte Reihe endet exakt an Width.
        var cumulative = 0.0;
        var left = 0.0;
        for (var j = first; j <= last; j++)
        {
            cumulative += _aspect[j] * imageHeight + Spacing;
            var right = justify && j == last ? Width : Math.Round(cumulative);
            _itemRow[j] = row;
            _itemX[j] = left;
            _itemWidth[j] = Math.Max(1, right - left);
            left = right;
        }

        return top + cellHeight;
    }

    private void EnsureCapacity(int n)
    {
        if (_aspect.Length >= n)
        {
            return;
        }

        var size = Math.Max(n, 64);
        _aspect = new double[size];
        _itemX = new double[size];
        _itemWidth = new double[size];
        _itemRow = new int[size];
        _rowTop = new double[size];
        _rowHeight = new double[size];
        _rowFirst = new int[size];
    }
}
