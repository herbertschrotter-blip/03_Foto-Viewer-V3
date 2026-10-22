using System;
using System.Collections.Generic;
using System.Linq;

namespace Lumora.Stories;

/// <summary>Vorlagen der Collage-Blöcke der Story-Ansicht (Mockup 09, Klassen <c>b-big2</c>, <c>b-2big</c> …).</summary>
public enum StoryBlockTemplate
{
    /// <summary>Großes Bild links über zwei Reihen, zwei kleine rechts (2fr 1fr).</summary>
    BigPlusTwo,

    /// <summary>Zwei kleine links, großes Bild rechts über zwei Reihen (1fr 2fr).</summary>
    TwoPlusBig,

    /// <summary>Drei gleich breite Bilder nebeneinander.</summary>
    Three,

    /// <summary>Panorama allein über die volle Breite.</summary>
    Panorama,

    /// <summary>Zwei Hochformate nebeneinander plus ein Querformat (1fr 1fr 2fr).</summary>
    PortraitPair,

    /// <summary>Vier nebeneinander (1,4fr 1fr 1fr 1,4fr).</summary>
    Four,

    /// <summary>Zwei nebeneinander im eigenen Seitenverhältnis (Rest am Ende eines Tages, zwei Hochformate).</summary>
    Pair,

    /// <summary>Ein Bild über die volle Breite (einzelner Rest).</summary>
    Single,
}

/// <summary>Ein Foto für die Collage: Seitenverhältnis (Breite / Höhe nach EXIF-Drehung), Video, Wert der Highlight-Auswahl.</summary>
public readonly record struct StoryLayoutPhoto(int FileId, double Aspect, bool IsVideo = false, double Score = 0);

/// <summary>Platz eines Fotos im Block (DIP, relativ zur linken oberen Ecke des Blocks); <see cref="Index"/> = Position in der Eingabe.</summary>
public sealed record StoryLayoutTile(int Index, int FileId, double X, double Y, double Width, double Height, bool IsLarge);

/// <summary>Ein Collage-Block: Vorlage, Höhe und die Plätze seiner Fotos (lückenlos über die volle Breite).</summary>
public sealed record StoryLayoutBlock(StoryBlockTemplate Template, double Width, double Height, IReadOnlyList<StoryLayoutTile> Tiles);

/// <summary>
/// Reine Layout-Logik der Story-Collage (ADR-0060, Mockup 09): verteilt die Highlights eines Tages in Aufnahme-Reihenfolge
/// auf Blöcke mit Vorlagen wie im Mockup — groß+2, 2+groß, 3er, Panorama, Hochformat-Paar+Querformat, 4er — abwechselnd,
/// Panoramen allein über die volle Breite, zwei Hochformate nebeneinander. Jede Reihe eines Blocks füllt die Breite exakt
/// (Spalten über kumulierte Rundung auf ganze DIP), es bleiben nur die Fugen. Deterministisch: gleiche Eingabe, gleiche
/// Ausgabe; <c>seed</c> (z. B. Tagesnummer) verschiebt nur den Wechsel der Vorlagen, damit nicht jeder Tag gleich beginnt.
/// Höhen wie im Mockup bei 1000 DIP Breite, mit der Breite mitwachsend (0,6- bis 1,5-fach).
/// </summary>
public static class StoryLayoutEngine
{
    /// <summary>Ab diesem Seitenverhältnis gilt ein Foto als Panorama.</summary>
    public const double PanoramaAspect = 2.2;

    /// <summary>Unter diesem Seitenverhältnis gilt ein Foto als Hochformat.</summary>
    public const double PortraitAspect = 0.9;

    /// <summary>Breite, für die die Höhen des Mockups gelten.</summary>
    public const double ReferenceWidth = 1000;

    /// <summary>Fuge zwischen den Bildern (Mockup: 6 px).</summary>
    public const double DefaultGap = 6;

    /// <summary>Ohne bekannte Maße.</summary>
    public const double DefaultAspect = 4.0 / 3.0;

    /// <summary>Dreier-Vorlage je Platz im Viererwechsel (Platz 2 = 4er; passt der nicht, groß+2).</summary>
    private static readonly StoryBlockTemplate[] ThreeCycle =
        [StoryBlockTemplate.BigPlusTwo, StoryBlockTemplate.Three, StoryBlockTemplate.BigPlusTwo, StoryBlockTemplate.TwoPlusBig];

    /// <summary>Blöcke für <paramref name="photos"/> bei <paramref name="width"/> DIP Breite.</summary>
    public static IReadOnlyList<StoryLayoutBlock> Layout(IReadOnlyList<StoryLayoutPhoto> photos, double width, double gap = DefaultGap, int seed = 0)
    {
        ArgumentNullException.ThrowIfNull(photos);
        var blocks = new List<StoryLayoutBlock>();
        if (photos.Count == 0)
        {
            return blocks;
        }

        width = double.IsFinite(width) ? Math.Max(120, Math.Floor(width)) : ReferenceWidth;
        gap = double.IsFinite(gap) && gap >= 0 ? Math.Round(gap) : DefaultGap;
        var scale = Math.Clamp(width / ReferenceWidth, 0.6, 1.5);
        var step = Math.Abs(seed % 4);
        var i = 0;
        while (i < photos.Count)
        {
            if (IsPanorama(photos[i]))
            {
                blocks.Add(Panorama(photos, i, width, scale));
                i++;
                continue;
            }

            var run = 0;
            while (i + run < photos.Count && !IsPanorama(photos[i + run]))
            {
                run++;
            }

            var (template, count) = Choose(photos, i, run, step);
            blocks.Add(Build(template, photos, i, count, width, gap, scale));
            i += count;
            step++;
        }

        return blocks;
    }

    /// <summary>Gesamthöhe aller Blöcke samt Fugen dazwischen.</summary>
    public static double TotalHeight(IReadOnlyList<StoryLayoutBlock> blocks, double gap = DefaultGap)
        => blocks.Sum(b => b.Height) + Math.Max(0, blocks.Count - 1) * gap;

    public static bool IsPanorama(StoryLayoutPhoto photo) => Aspect(photo) >= PanoramaAspect;

    public static bool IsPortrait(StoryLayoutPhoto photo) => Aspect(photo) < PortraitAspect;

    private static double Aspect(StoryLayoutPhoto photo)
        => double.IsFinite(photo.Aspect) && photo.Aspect > 0 ? photo.Aspect : DefaultAspect;

    /// <summary>Vorlage und Zahl der Fotos für den nächsten Block aus einer Folge von <paramref name="run"/> Nicht-Panoramen.</summary>
    private static (StoryBlockTemplate Template, int Count) Choose(IReadOnlyList<StoryLayoutPhoto> photos, int start, int run, int step)
    {
        switch (run)
        {
            case 1:
                return (StoryBlockTemplate.Single, 1);
            case 2:
                return (StoryBlockTemplate.Pair, 2);
        }

        // Zwei Hochformate unter den nächsten drei: nebeneinander (mit Querformat dazu bzw. drei Hochformate als 3er).
        var portraits = Enumerable.Range(start, 3).Count(k => IsPortrait(photos[k]));
        if (portraits == 2)
        {
            return (StoryBlockTemplate.PortraitPair, 3);
        }

        if (portraits == 3)
        {
            return (StoryBlockTemplate.Three, 3);
        }

        // Wechsel: groß+2, 3er, 4er, 2+groß, … — Vier nur, wenn höchstens ein Hochformat dabei ist (zwei schmale Spalten
        // würden Hochformate stark beschneiden) und danach kein einzelnes Foto bzw. gleich wieder ein 4er übrig bleibt.
        var slot = step % 4;
        var fourFits = run >= 4 && Enumerable.Range(start, 4).Count(k => IsPortrait(photos[k])) <= 1;
        if (run == 4)
        {
            return fourFits ? (StoryBlockTemplate.Four, 4) : (StoryBlockTemplate.Pair, 2);
        }

        if (slot == 2 && fourFits && run - 4 is not (1 or 4))
        {
            return (StoryBlockTemplate.Four, 4);
        }

        return (ThreeCycle[slot], 3);
    }

    private static StoryLayoutBlock Panorama(IReadOnlyList<StoryLayoutPhoto> photos, int index, double width, double scale)
    {
        var height = Math.Round(Math.Clamp(width / Aspect(photos[index]), 140 * scale, 280 * scale));
        return new StoryLayoutBlock(StoryBlockTemplate.Panorama, width, height,
            [new StoryLayoutTile(index, photos[index].FileId, 0, 0, width, height, IsLarge: true)]);
    }

    private static StoryLayoutBlock Build(StoryBlockTemplate template, IReadOnlyList<StoryLayoutPhoto> photos, int start, int count, double width, double gap, double scale)
    {
        var order = Enumerable.Range(start, count).ToList();
        switch (template)
        {
            case StoryBlockTemplate.Single:
            {
                var height = Math.Round(Math.Clamp(width / Aspect(photos[start]), 220 * scale, 440 * scale));
                return new StoryLayoutBlock(template, width, height, [Tile(photos, start, 0, 0, width, height, true)]);
            }

            case StoryBlockTemplate.Pair:
            {
                var a = Aspect(photos[start]);
                var b = Aspect(photos[start + 1]);
                var height = Math.Round(Math.Clamp((width - gap) / (a + b), 200 * scale, 420 * scale));
                var (x, w) = Columns([a, b], width, gap);
                return new StoryLayoutBlock(template, width, height,
                [
                    Tile(photos, start, x[0], 0, w[0], height, false),
                    Tile(photos, start + 1, x[1], 0, w[1], height, false),
                ]);
            }

            case StoryBlockTemplate.Three:
            {
                var height = Math.Round(230 * scale);
                return Row(template, photos, order, [1, 1, 1], width, gap, height);
            }

            case StoryBlockTemplate.Four:
            {
                var height = Math.Round(200 * scale);
                return Row(template, photos, order, [1.4, 1, 1, 1.4], width, gap, height);
            }

            case StoryBlockTemplate.PortraitPair:
            {
                // Hochformate in die schmalen Spalten (Reihenfolge der beiden bleibt), Querformat in die breite.
                var portraits = order.Where(k => IsPortrait(photos[k])).ToList();
                var landscape = order.First(k => !IsPortrait(photos[k]));
                var height = Math.Round(340 * scale);
                return Row(template, photos, [.. portraits, landscape], [1, 1, 2], width, gap, height);
            }

            default:
            {
                // groß+2 bzw. 2+groß: das Foto mit dem höchsten Wert (bei Gleichstand das erste) kommt groß.
                var big = order.OrderByDescending(k => photos[k].Score).ThenBy(k => k).First();
                var small = order.Where(k => k != big).ToList();
                var rowHeight = Math.Round(190 * scale);
                var height = rowHeight * 2 + gap;
                var bigLeft = template == StoryBlockTemplate.BigPlusTwo;
                var (x, w) = Columns(bigLeft ? [2, 1] : [1, 2], width, gap);
                var bigColumn = bigLeft ? 0 : 1;
                var smallColumn = 1 - bigColumn;
                return new StoryLayoutBlock(template, width, height,
                [
                    Tile(photos, big, x[bigColumn], 0, w[bigColumn], height, true),
                    Tile(photos, small[0], x[smallColumn], 0, w[smallColumn], rowHeight, false),
                    Tile(photos, small[1], x[smallColumn], rowHeight + gap, w[smallColumn], height - rowHeight - gap, false),
                ]);
            }
        }
    }

    private static StoryLayoutBlock Row(StoryBlockTemplate template, IReadOnlyList<StoryLayoutPhoto> photos, IReadOnlyList<int> order, double[] fractions, double width, double gap, double height)
    {
        var (x, w) = Columns(fractions, width, gap);
        var tiles = new List<StoryLayoutTile>(order.Count);
        for (var c = 0; c < order.Count; c++)
        {
            tiles.Add(Tile(photos, order[c], x[c], 0, w[c], height, fractions[c] > 1.5));
        }

        return new StoryLayoutBlock(template, width, height, tiles);
    }

    private static StoryLayoutTile Tile(IReadOnlyList<StoryLayoutPhoto> photos, int index, double x, double y, double width, double height, bool large)
        => new(index, photos[index].FileId, x, y, width, height, large);

    /// <summary>
    /// Spalten mit Anteilen <paramref name="fractions"/>: Grenzen über kumulierte Rundung auf ganze DIP, damit die Breiten
    /// samt Fugen exakt <paramref name="width"/> ergeben.
    /// </summary>
    internal static (double[] X, double[] Width) Columns(IReadOnlyList<double> fractions, double width, double gap)
    {
        var n = fractions.Count;
        var available = width - gap * (n - 1);
        var total = fractions.Sum();
        var x = new double[n];
        var w = new double[n];
        var previous = 0.0;
        var cumulative = 0.0;
        for (var c = 0; c < n; c++)
        {
            cumulative += fractions[c];
            var boundary = c == n - 1 ? available : Math.Round(available * cumulative / total);
            x[c] = previous + c * gap;
            w[c] = boundary - previous;
            previous = boundary;
        }

        return (x, w);
    }
}
