using Lumora.Services.Assistant;
using Lumora.Tests.Stories;

namespace Lumora.Tests.Assistant;

/// <summary>
/// Collage-Bild (ADR-0063): Maße je Format/Größe, Layout ohne Überlappung und lückenlos (jede Reihe füllt die Breite,
/// die Reihen die Höhe), Titelbild doppelt groß, Zeitstrahl nach Seitenverhältnis; Collage-Dienst: nur Fotos des
/// Bereichs, Auftrag an den Renderer, versteckte Bibliothek nur im Container.
/// </summary>
public sealed class CollageTests
{
    [Theory]
    [InlineData(CollageFormat.Wide16x9, CollageSize.FullHd, 1920, 1080, 96)]
    [InlineData(CollageFormat.Wide16x9, CollageSize.Uhd4K, 3840, 2160, 96)]
    [InlineData(CollageFormat.A4Landscape, CollageSize.Print300, 3508, 2481, 300)]
    [InlineData(CollageFormat.A4Portrait, CollageSize.Print300, 2481, 3508, 300)]
    [InlineData(CollageFormat.Square, CollageSize.FullHd, 1920, 1920, 96)]
    public void Masse_je_Format_und_Groesse(CollageFormat format, CollageSize size, int width, int height, int dpi)
        => CollageLayout.Dimensions(format, size).Should().Be((width, height, dpi));

    public static TheoryData<CollageArrangement, int, bool> Cases()
    {
        var data = new TheoryData<CollageArrangement, int, bool>();
        foreach (var arrangement in Enum.GetValues<CollageArrangement>())
        {
            foreach (var count in new[] { 1, 2, 3, 5, 7, 12, 13, 24, 36, 47, 48 })
            {
                data.Add(arrangement, count, count % 2 == 0);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Layout_ohne_Ueberlappung_und_lueckenlos(CollageArrangement arrangement, int count, bool title)
    {
        var random = new Random(count);
        var aspects = Enumerable.Range(0, count).Select(_ => random.Next(3) switch { 0 => 4.0 / 3, 1 => 3.0 / 4, _ => 16.0 / 9 }).ToList();
        foreach (var (format, size) in new[] { (CollageFormat.Wide16x9, CollageSize.FullHd), (CollageFormat.A4Portrait, CollageSize.Print300), (CollageFormat.Square, CollageSize.Uhd4K) })
        {
            var (w, h, dpi) = CollageLayout.Dimensions(format, size);
            var layout = CollageLayout.Compute(w, h, dpi, aspects, arrangement, title);

            layout.Cells.Select(c => c.Index).Should().BeEquivalentTo(Enumerable.Range(0, count), "jedes Foto genau einmal");
            var content = layout.Content;
            layout.Title.HasValue.Should().Be(title);
            if (layout.Title is { } t)
            {
                t.Bottom.Should().BeLessThan(content.Y);
            }

            foreach (var cell in layout.Cells)
            {
                cell.Rect.Width.Should().BePositive();
                cell.Rect.Height.Should().BePositive();
                cell.Rect.X.Should().BeGreaterThanOrEqualTo(content.X);
                cell.Rect.Y.Should().BeGreaterThanOrEqualTo(content.Y);
                cell.Rect.Right.Should().BeLessThanOrEqualTo(content.Right);
                cell.Rect.Bottom.Should().BeLessThanOrEqualTo(content.Bottom);
                layout.Cells.Where(o => o.Index != cell.Index).Should().NotContain(o => o.Rect.Overlaps(cell.Rect), $"{arrangement} {count}: keine Überlappung");
            }

            // lückenlos: Fläche der Fotos + Fugen = Inhaltsfläche; jede Zeile (y) wird links bis rechts gefüllt
            for (var y = content.Y; y < content.Bottom; y += Math.Max(1, content.Height / 17))
            {
                var row = layout.Cells.Where(c => c.Rect.Y <= y && c.Rect.Bottom > y).OrderBy(c => c.Rect.X).ToList();
                if (row.Count == 0)
                {
                    continue; // Fuge zwischen zwei Reihen
                }

                row[0].Rect.X.Should().Be(content.X);
                row[^1].Rect.Right.Should().Be(content.Right, $"{arrangement} {count} {format}: Reihe füllt die Breite");
                for (var i = 1; i < row.Count; i++)
                {
                    (row[i].Rect.X - row[i - 1].Rect.Right).Should().Be(layout.Gap);
                }
            }

            layout.Cells.Min(c => c.Rect.Y).Should().Be(content.Y);
            layout.Cells.Max(c => c.Rect.Bottom).Should().Be(content.Bottom, "die Reihen füllen die Höhe");
        }
    }

    [Fact]
    public void Titelbild_ist_doppelt_so_gross_und_Zeitstrahl_folgt_dem_Seitenverhaeltnis()
    {
        var hero = CollageLayout.Compute(1920, 1080, 96, Enumerable.Repeat(4.0 / 3, 12).ToList(), CollageArrangement.Hero, false);
        var big = hero.Cells.Single(c => c.Index == 0).Rect;
        var small = hero.Cells.Single(c => c.Index == 1).Rect;
        big.Width.Should().BeGreaterThan(2 * small.Width - 1);
        big.Height.Should().BeGreaterThan(2 * small.Height - 1);

        var timeline = CollageLayout.Compute(1920, 1080, 96, [16.0 / 9, 3.0 / 4, 16.0 / 9, 3.0 / 4], CollageArrangement.Timeline, false);
        var cells = timeline.Cells.OrderBy(c => c.Index).ToList();
        cells[0].Rect.Width.Should().BeGreaterThan(cells[1].Rect.Width, "Querformat breiter als Hochformat in derselben Reihe");
    }

    [Fact]
    public async Task Collage_Dienst_nur_Fotos_des_Bereichs_und_versteckt_nur_im_Container()
    {
        using var a = await AssistantWorld.CreateAsync();
        var files = a.W.Add(StoryWorld.Burst(StoryWorld.Utc(2024, 7, 1), 6, videos: 2));
        var other = (await a.W.Fx.CreateLibraryService().CreateAsync("Andere", a.W.Fx.OutsideDir)).Id;
        var foreign = a.W.Add(StoryWorld.Burst(StoryWorld.Utc(2024, 7, 1), 2), other);
        await a.W.Fx.CreateLibraryService().SetActiveAsync(a.W.LibraryId);

        var photos = await a.Collage.LoadPhotosAsync(files.Concat(foreign).ToList());
        photos.Select(p => p.FileId).Should().Equal(files.Take(4), "keine Videos, keine Dateien anderer Bibliotheken");
        photos[0].Aspect.Should().BeApproximately(4000.0 / 3000, 0.001);
        photos[0].LocalTime.Should().Be(new DateTime(2024, 7, 1, 10, 0, 0));
        (await a.Collage.RankAsync(files, 10)).Should().OnlyContain(id => files.Contains(id));

        var target = Path.Combine(a.W.Fx.OutsideDir, "Collagen", "Anna 2024.jpg");
        var saved = await a.Collage.SaveAsync(new CollageOptions(CollageFormat.Square, CollageSize.FullHd, 12, CollageArrangement.Grid, ShowTitle: true), photos, "Anna · 2024", target);
        saved.Should().Be(new CollageSaveResult(target, 1920, 1920, 4));
        var job = a.Renderer.Jobs.Single();
        job.Title.Should().Be("Anna · 2024");
        job.TitleNote.Should().Be("4 Fotos");
        job.Format.Should().Be(CollageImageFormat.Jpeg);
        job.Layout.Cells.Should().HaveCount(4);
        CollageService.SuggestFileName("Anna · 1985–2005").Should().Be("Anna 1985-2005.jpg");
        CollageService.SuggestFileName("a/b:c").Should().Be("a b c.jpg");

        // versteckte Bibliothek: Standardordner im Bibliotheksordner, außerhalb wird abgelehnt
        a.W.Catalog.SetHidden(true);
        var root = a.W.Fx.LibraryRoot;
        (await a.Collage.GetDefaultFolderAsync()).Should().StartWith(Path.GetFullPath(root).TrimEnd('\\'));
        (await a.Collage.IsAllowedTargetAsync(Path.Combine(root, "Collagen", "x.jpg"))).Should().BeTrue();
        (await a.Collage.IsAllowedTargetAsync(Path.Combine(Path.GetTempPath(), "x.jpg"))).Should().BeFalse();
        await FluentActions.Awaiting(() => a.Collage.SaveAsync(new CollageOptions(), photos, null, Path.Combine(Path.GetTempPath(), "x.jpg")))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*Container*");
        a.Renderer.Jobs.Should().HaveCount(1, "nichts außerhalb gerendert");
        a.W.Catalog.SetHidden(false);
        (await a.Collage.GetDefaultFolderAsync()).Should().EndWith(Path.Combine("Lumora", "Collagen"));
    }
}
