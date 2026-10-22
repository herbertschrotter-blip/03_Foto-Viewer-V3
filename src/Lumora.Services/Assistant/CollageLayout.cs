namespace Lumora.Services.Assistant;

/// <summary>Format des Collage-Bilds (Mockup 12).</summary>
public enum CollageFormat
{
    Wide16x9 = 0,
    A4Landscape = 1,
    A4Portrait = 2,
    Square = 3,
}

/// <summary>Größe: lange Kante 1920 (Full HD), 3840 (4K) bzw. 3508 Pixel (A4 bei 300 dpi).</summary>
public enum CollageSize
{
    FullHd = 0,
    Uhd4K = 1,
    Print300 = 2,
}

/// <summary>Anordnung.</summary>
public enum CollageArrangement
{
    /// <summary>Großes Titelbild (2 × 2 Felder oben links), die übrigen im Raster.</summary>
    Hero = 0,

    /// <summary>Gleichmäßiges Raster.</summary>
    Grid = 1,

    /// <summary>Zeitstrahl: Reihen in Aufnahme-Reihenfolge, Breite nach Seitenverhältnis (Blocksatz).</summary>
    Timeline = 2,
}

/// <summary>Rechteck in ganzen Pixeln.</summary>
public readonly record struct CollageRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;

    public bool Overlaps(CollageRect other) => X < other.Right && other.X < Right && Y < other.Bottom && other.Y < Bottom;
}

/// <summary>Platz eines Fotos (Index in der Foto-Liste).</summary>
public readonly record struct CollageCell(int Index, CollageRect Rect);

/// <summary>Ergebnis des Layouts: Bildgröße, Überschrift (falls an) und Plätze.</summary>
public sealed record CollageLayoutResult(int Width, int Height, int Dpi, CollageRect? Title, CollageRect Content, int Gap, IReadOnlyList<CollageCell> Cells);

/// <summary>
/// Layout des Collage-Bilds (ADR-0063), rein und getestet: ganze Pixel, keine Überlappung, jede Reihe füllt die
/// Inhaltsbreite und die Reihen die Inhaltshöhe exakt (nur die Fugen bleiben) — lückenlos. Fotos werden in ihren Platz
/// eingepasst (beschnitten), das Raster richtet sich nach einem Ziel-Seitenverhältnis der Felder von 4:3.
/// </summary>
public static class CollageLayout
{
    public const double TargetCellAspect = 4.0 / 3.0;

    /// <summary>Pixelmaße zu Format und Größe.</summary>
    public static (int Width, int Height, int Dpi) Dimensions(CollageFormat format, CollageSize size)
    {
        var longEdge = size switch
        {
            CollageSize.FullHd => 1920,
            CollageSize.Uhd4K => 3840,
            _ => 3508,
        };
        var ratio = format switch
        {
            CollageFormat.Wide16x9 => 16.0 / 9.0,
            CollageFormat.A4Landscape => Math.Sqrt(2),
            CollageFormat.A4Portrait => 1 / Math.Sqrt(2),
            _ => 1.0,
        };
        var dpi = size == CollageSize.Print300 ? 300 : 96;
        return ratio >= 1
            ? (longEdge, (int)Math.Round(longEdge / ratio), dpi)
            : ((int)Math.Round(longEdge * ratio), longEdge, dpi);
    }

    /// <summary>Plätze für <paramref name="aspects"/>.Count Fotos (Seitenverhältnisse nur für den Zeitstrahl).</summary>
    public static CollageLayoutResult Compute(int width, int height, int dpi, IReadOnlyList<double> aspects, CollageArrangement arrangement, bool showTitle)
    {
        ArgumentNullException.ThrowIfNull(aspects);
        if (width < 16 || height < 16)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Collage zu klein.");
        }

        var shortEdge = Math.Min(width, height);
        var margin = Math.Max(4, (int)Math.Round(shortEdge * 0.025));
        var gap = Math.Max(2, (int)Math.Round(shortEdge * 0.006));
        CollageRect? title = null;
        var top = margin;
        if (showTitle)
        {
            var titleHeight = Math.Max(12, (int)Math.Round(shortEdge * 0.065));
            title = new CollageRect(margin, margin, width - (2 * margin), titleHeight);
            top = margin + titleHeight + gap;
        }

        var content = new CollageRect(margin, top, width - (2 * margin), height - top - margin);
        var n = aspects.Count;
        if (n == 0 || content.Width < 4 || content.Height < 4)
        {
            return new CollageLayoutResult(width, height, dpi, title, content, gap, []);
        }

        var cells = arrangement switch
        {
            CollageArrangement.Hero when n >= 3 => Hero(content, gap, n),
            CollageArrangement.Timeline => Timeline(content, gap, aspects),
            _ => Grid(content, gap, n),
        };
        return new CollageLayoutResult(width, height, dpi, title, content, gap, cells);
    }

    /// <summary>Raster: Reihen mit fast gleich vielen Feldern (volle Breite), Zahl der Reihen nach dem Ziel-Seitenverhältnis.</summary>
    private static List<CollageCell> Grid(CollageRect content, int gap, int n)
    {
        var bestRows = 1;
        var bestCost = double.MaxValue;
        for (var rows = 1; rows <= n; rows++)
        {
            var columns = (int)Math.Ceiling((double)n / rows);
            var cellAspect = ((double)content.Width / columns) / ((double)content.Height / rows);
            var cost = Math.Abs(Math.Log(cellAspect / TargetCellAspect)) + (0.08 * ((columns * rows) - n));
            if (cost < bestCost)
            {
                (bestCost, bestRows) = (cost, rows);
            }
        }

        var counts = Distribute(n, bestRows);
        return Rows(content, gap, counts.Select(c => Enumerable.Repeat(1.0, c).ToList()).ToList(), 0);
    }

    /// <summary>Großes Titelbild 2 × 2 Felder oben links, daneben und darunter das Raster; eine unvollständige letzte Reihe wird gestreckt.</summary>
    private static List<CollageCell> Hero(CollageRect content, int gap, int n)
    {
        (int Columns, int Rows)? best = null;
        var bestCost = double.MaxValue;
        var rest = n - 1;
        for (var columns = 3; columns <= 12; columns++)
        {
            var beside = 2 * (columns - 2);
            int rows, partial;
            if (rest <= beside)
            {
                if (rest != beside)
                {
                    continue;
                }

                (rows, partial) = (2, 0);
            }
            else
            {
                var below = rest - beside;
                rows = 2 + (int)Math.Ceiling((double)below / columns);
                partial = below % columns;
            }

            var cellAspect = ((double)content.Width / columns) / ((double)content.Height / rows);
            var cost = Math.Abs(Math.Log(cellAspect / TargetCellAspect)) + (partial == 0 ? 0 : 0.35 * Math.Log((double)columns / partial));
            if (cost < bestCost)
            {
                (bestCost, best) = (cost, (columns, rows));
            }
        }

        if (best is not { } layout)
        {
            return Grid(content, gap, n);
        }

        var (cols, rowCount) = layout;
        var ys = Split(content.Y, content.Height, rowCount, gap, Enumerable.Repeat(1.0, rowCount).ToList());
        var xs = Split(content.X, content.Width, cols, gap, Enumerable.Repeat(1.0, cols).ToList());
        var cells = new List<CollageCell>(n)
        {
            new(0, new CollageRect(xs[0].Start, ys[0].Start, xs[1].Start + xs[1].Length - xs[0].Start, ys[1].Start + ys[1].Length - ys[0].Start)),
        };
        var index = 1;
        for (var r = 0; r < 2 && index < n; r++)
        {
            for (var c = 2; c < cols && index < n; c++)
            {
                cells.Add(new CollageCell(index++, new CollageRect(xs[c].Start, ys[r].Start, xs[c].Length, ys[r].Length)));
            }
        }

        for (var r = 2; r < rowCount && index < n; r++)
        {
            var inRow = Math.Min(cols, n - index);
            var rowXs = inRow == cols ? xs : Split(content.X, content.Width, inRow, gap, Enumerable.Repeat(1.0, inRow).ToList());
            for (var c = 0; c < inRow; c++)
            {
                cells.Add(new CollageCell(index++, new CollageRect(rowXs[c].Start, ys[r].Start, rowXs[c].Length, ys[r].Length)));
            }
        }

        return cells;
    }

    /// <summary>Zeitstrahl: Fotos in Reihenfolge auf Reihen verteilt, Breiten nach Seitenverhältnis, Reihen gleich hoch.</summary>
    private static List<CollageCell> Timeline(CollageRect content, int gap, IReadOnlyList<double> aspects)
    {
        var n = aspects.Count;
        var safe = aspects.Select(a => double.IsFinite(a) && a > 0.1 ? Math.Min(a, 10) : TargetCellAspect).ToList();
        var bestRows = 1;
        var bestCost = double.MaxValue;
        for (var rows = 1; rows <= n; rows++)
        {
            var counts = Distribute(n, rows);
            var rowHeight = (double)content.Height / rows;
            var cost = 0.0;
            var offset = 0;
            foreach (var count in counts)
            {
                var sum = safe.Skip(offset).Take(count).Sum();
                offset += count;
                var naturalWidth = sum * rowHeight; // Breite bei unbeschnittenen Fotos
                cost += Math.Abs(Math.Log(naturalWidth / content.Width));
            }

            cost /= rows;
            if (cost < bestCost)
            {
                (bestCost, bestRows) = (cost, rows);
            }
        }

        var distribution = Distribute(n, bestRows);
        var weights = new List<List<double>>();
        var start = 0;
        foreach (var count in distribution)
        {
            weights.Add(safe.Skip(start).Take(count).ToList());
            start += count;
        }

        return Rows(content, gap, weights, 0);
    }

    /// <summary>Reihen gleicher Höhe, je Reihe Breiten nach Gewicht; Indizes fortlaufend ab <paramref name="firstIndex"/>.</summary>
    private static List<CollageCell> Rows(CollageRect content, int gap, IReadOnlyList<List<double>> rows, int firstIndex)
    {
        var cells = new List<CollageCell>();
        var ys = Split(content.Y, content.Height, rows.Count, gap, Enumerable.Repeat(1.0, rows.Count).ToList());
        var index = firstIndex;
        for (var r = 0; r < rows.Count; r++)
        {
            var xs = Split(content.X, content.Width, rows[r].Count, gap, rows[r]);
            foreach (var (s, length) in xs)
            {
                cells.Add(new CollageCell(index++, new CollageRect(s, ys[r].Start, length, ys[r].Length)));
            }
        }

        return cells;
    }

    /// <summary><paramref name="n"/> auf <paramref name="parts"/> Reihen: die ersten bekommen einen mehr.</summary>
    private static List<int> Distribute(int n, int parts)
    {
        var list = new List<int>(parts);
        for (var i = 0; i < parts; i++)
        {
            list.Add((n / parts) + (i < n % parts ? 1 : 0));
        }

        return list;
    }

    /// <summary>
    /// Teilt [<paramref name="start"/>, <paramref name="start"/> + <paramref name="total"/>) in Stücke nach Gewicht mit
    /// <paramref name="gap"/> dazwischen — kumulativ gerundet, die Summe aus Stücken und Fugen ist exakt <paramref name="total"/>.
    /// </summary>
    public static List<(int Start, int Length)> Split(int start, int total, int count, int gap, IReadOnlyList<double> weights)
    {
        var available = Math.Max(count, total - (gap * (count - 1)));
        var sum = weights.Take(count).Sum();
        var result = new List<(int, int)>(count);
        var cumulative = 0.0;
        var previous = 0;
        for (var i = 0; i < count; i++)
        {
            cumulative += weights[i];
            var boundary = i == count - 1 ? available : (int)Math.Round(available * cumulative / sum);
            result.Add((start + previous + (gap * i), Math.Max(1, boundary - previous)));
            previous = boundary;
        }

        return result;
    }
}
