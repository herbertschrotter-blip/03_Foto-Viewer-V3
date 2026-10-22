using Lumora.Stories;

namespace Lumora.Wpf.Tests.Stories;

/// <summary>
/// <see cref="StoryLayoutEngine"/> (ADR-0060): Vorlagen wie im Mockup 09, Panoramen allein, Hochformate nebeneinander,
/// Videos wie Fotos, 1/2/3/n Fotos, keine Lücken, Breiten addieren sich exakt, deterministisch.
/// </summary>
public sealed class StoryLayoutEngineTests
{
    private const double Gap = StoryLayoutEngine.DefaultGap;

    private static StoryLayoutPhoto L(int id, double score = 0) => new(id, 1.5, false, score);

    private static StoryLayoutPhoto P(int id) => new(id, 0.67);

    private static StoryLayoutPhoto Pano(int id) => new(id, 4.0);

    private static StoryLayoutPhoto V(int id) => new(id, 16.0 / 9.0, IsVideo: true);

    [Fact]
    public void Panorama_steht_allein_ueber_die_volle_Breite()
    {
        var blocks = StoryLayoutEngine.Layout([L(1), L(2), L(3), Pano(4), L(5), L(6), L(7)], 1000);

        var pano = blocks.Single(b => b.Template == StoryBlockTemplate.Panorama);
        pano.Tiles.Should().ContainSingle().Which.FileId.Should().Be(4);
        pano.Tiles[0].X.Should().Be(0);
        pano.Tiles[0].Width.Should().Be(1000);
        pano.Height.Should().BeInRange(140, 280);
        blocks.Should().HaveCount(3, "drei vor dem Panorama, Panorama, drei danach");
        AssertCoversAll(blocks, 7);
    }

    [Fact]
    public void Zwei_Hochformate_kommen_nebeneinander_mit_einem_Querformat()
    {
        var blocks = StoryLayoutEngine.Layout([P(1), L(2), P(3)], 1000);

        var block = blocks.Should().ContainSingle().Subject;
        block.Template.Should().Be(StoryBlockTemplate.PortraitPair);
        var portraits = block.Tiles.Where(t => t.FileId is 1 or 3).OrderBy(t => t.X).ToList();
        portraits.Should().HaveCount(2);
        portraits[0].Y.Should().Be(portraits[1].Y, "nebeneinander in derselben Reihe");
        portraits[1].X.Should().Be(portraits[0].X + portraits[0].Width + Gap);
        (portraits[0].Width / portraits[0].Height).Should().BeLessThan(1, "schmale Spalten für Hochformate");
        block.Tiles.Single(t => t.FileId == 2).Width.Should().BeGreaterThan(portraits[0].Width, "Querformat in der breiten Spalte");
    }

    [Fact]
    public void Zwei_Hochformate_allein_stehen_als_Paar_nebeneinander()
    {
        var block = StoryLayoutEngine.Layout([P(1), P(2)], 800).Should().ContainSingle().Subject;

        block.Template.Should().Be(StoryBlockTemplate.Pair);
        block.Tiles[0].Y.Should().Be(block.Tiles[1].Y);
        (block.Tiles[0].Width + Gap + block.Tiles[1].Width).Should().Be(800);
    }

    [Fact]
    public void Videos_werden_wie_Fotos_verteilt_und_behalten_ihre_Id()
    {
        var blocks = StoryLayoutEngine.Layout([L(1), V(2), L(3), V(4), L(5), L(6)], 1000);

        AssertCoversAll(blocks, 6);
        blocks.SelectMany(b => b.Tiles).Select(t => t.FileId).Should().Contain([2, 4]);
    }

    [Theory]
    [InlineData(1, new[] { StoryBlockTemplate.Single })]
    [InlineData(2, new[] { StoryBlockTemplate.Pair })]
    [InlineData(3, new[] { StoryBlockTemplate.BigPlusTwo })]
    [InlineData(4, new[] { StoryBlockTemplate.Four })]
    [InlineData(5, new[] { StoryBlockTemplate.BigPlusTwo, StoryBlockTemplate.Pair })]
    [InlineData(6, new[] { StoryBlockTemplate.BigPlusTwo, StoryBlockTemplate.Three })]
    public void Anzahl_Fotos_ergibt_die_Vorlagen(int count, StoryBlockTemplate[] expected)
    {
        var photos = Enumerable.Range(1, count).Select(i => L(i)).ToList();

        var blocks = StoryLayoutEngine.Layout(photos, 1000);

        blocks.Select(b => b.Template).Should().Equal(expected);
        AssertCoversAll(blocks, count);
    }

    [Fact]
    public void Viele_Fotos_wechseln_die_Vorlagen_ab_und_lassen_keines_aus()
    {
        var photos = Enumerable.Range(1, 40).Select(i => L(i, i % 5)).ToList();

        var blocks = StoryLayoutEngine.Layout(photos, 1000);

        AssertCoversAll(blocks, 40);
        blocks.Select(b => b.Template).Distinct().Should().HaveCountGreaterThanOrEqualTo(3, "abwechslungsreich");
        blocks.Zip(blocks.Skip(1)).Count(p => p.First.Template == p.Second.Template).Should().Be(0, "nie zweimal dieselbe Vorlage hintereinander");
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(733)]
    [InlineData(1581)]
    [InlineData(421)]
    public void Keine_Luecken_Breiten_addieren_sich_exakt(double width)
    {
        var photos = new List<StoryLayoutPhoto> { L(1, 3), P(2), L(3), P(4), Pano(5), L(6), V(7), L(8), L(9), L(10), P(11), P(12), P(13), L(14) };

        var blocks = StoryLayoutEngine.Layout(photos, width);

        AssertCoversAll(blocks, photos.Count);
        foreach (var block in blocks)
        {
            AssertNoGaps(block, Math.Floor(width));
        }
    }

    [Fact]
    public void Gross_bekommt_das_Foto_mit_dem_hoechsten_Wert()
    {
        var block = StoryLayoutEngine.Layout([L(1, 0.2), L(2, 0.9), L(3, 0.5)], 1000).Single();

        block.Template.Should().Be(StoryBlockTemplate.BigPlusTwo);
        var big = block.Tiles.Single(t => t.IsLarge);
        big.FileId.Should().Be(2);
        big.Height.Should().Be(block.Height, "über beide Reihen");
    }

    [Fact]
    public void Gleiche_Eingabe_gleiche_Ausgabe_seed_verschiebt_nur_den_Wechsel()
    {
        var photos = Enumerable.Range(1, 17).Select(i => i % 4 == 0 ? P(i) : L(i, i % 3)).ToList();

        var first = StoryLayoutEngine.Layout(photos, 1000, seed: 2);
        var second = StoryLayoutEngine.Layout(photos, 1000, seed: 2);
        var other = StoryLayoutEngine.Layout(photos, 1000, seed: 3);

        second.Should().BeEquivalentTo(first, o => o.WithStrictOrdering());
        AssertCoversAll(other, 17);
    }

    [Fact]
    public void Leere_Eingabe_ergibt_keine_Bloecke_und_Hoehe_waechst_mit_der_Breite()
    {
        StoryLayoutEngine.Layout([], 1000).Should().BeEmpty();
        var narrow = StoryLayoutEngine.Layout([L(1), L(2), L(3)], 800).Single();
        var wide = StoryLayoutEngine.Layout([L(1), L(2), L(3)], 1400).Single();
        wide.Height.Should().BeGreaterThan(narrow.Height);
    }

    /// <summary>Jedes Foto genau einmal, Indizes passen zur Eingabe.</summary>
    private static void AssertCoversAll(IReadOnlyList<StoryLayoutBlock> blocks, int count)
    {
        var tiles = blocks.SelectMany(b => b.Tiles).ToList();
        tiles.Select(t => t.Index).Should().BeEquivalentTo(Enumerable.Range(0, count), "jedes Foto genau einmal");
        tiles.Should().OnlyContain(t => t.Width > 0 && t.Height > 0);
    }

    /// <summary>
    /// Lückenlos: jede Kachel endet rechts am Rand oder eine Fuge vor einer Nachbar-Kachel (gleiche Höhe überlappend), unten
    /// am Blockende oder eine Fuge vor einer Kachel darunter; keine Überlappung; jede Reihe ist exakt so breit wie der Block.
    /// </summary>
    private static void AssertNoGaps(StoryLayoutBlock block, double width)
    {
        block.Width.Should().Be(width);
        foreach (var tile in block.Tiles)
        {
            tile.X.Should().BeGreaterThanOrEqualTo(0);
            (tile.X + tile.Width).Should().BeLessThanOrEqualTo(width + 0.001);
            var right = tile.X + tile.Width;
            var bottom = tile.Y + tile.Height;
            var touchesRight = Math.Abs(right - width) < 0.001
                || block.Tiles.Any(o => o != tile && Math.Abs(o.X - (right + Gap)) < 0.001 && o.Y < bottom && o.Y + o.Height > tile.Y);
            touchesRight.Should().BeTrue($"{block.Template}: Kachel {tile.FileId} rechts bündig oder mit Fuge zum Nachbarn");
            var touchesBottom = Math.Abs(bottom - block.Height) < 0.001
                || block.Tiles.Any(o => o != tile && Math.Abs(o.Y - (bottom + Gap)) < 0.001 && o.X < right && o.X + o.Width > tile.X);
            touchesBottom.Should().BeTrue($"{block.Template}: Kachel {tile.FileId} unten bündig oder mit Fuge zur Kachel darunter");
            foreach (var other in block.Tiles.Where(o => o != tile))
            {
                var overlaps = tile.X < other.X + other.Width && other.X < right && tile.Y < other.Y + other.Height && other.Y < bottom;
                overlaps.Should().BeFalse($"{block.Template}: Kacheln {tile.FileId} und {other.FileId} überlappen nicht");
            }
        }

        // Summe der Breiten je Reihe (Kacheln mit gleichem Y) plus Fugen = Blockbreite (Spalten über zwei Reihen zählen in jeder)
        foreach (var y in block.Tiles.Select(t => t.Y).Distinct())
        {
            var row = block.Tiles.Where(t => t.Y <= y && t.Y + t.Height > y).ToList();
            (row.Sum(t => t.Width) + Gap * (row.Count - 1)).Should().BeApproximately(width, 0.001, $"{block.Template}: Reihe bei y={y}");
        }
    }
}
