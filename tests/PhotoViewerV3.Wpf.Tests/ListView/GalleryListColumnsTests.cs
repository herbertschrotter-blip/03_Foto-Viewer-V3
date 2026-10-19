using PhotoViewerV3.Gallery.Data;
using PhotoViewerV3.Gallery.ListView;

namespace PhotoViewerV3.Wpf.Tests.ListView;

/// <summary>
/// Spalten- und Sortier-Logik der Listenansicht: Kopfzeilenklick auf Name/Geändert/Größe sortiert über die
/// Galerie-Sortierung (gesamte Trefferliste, erst aufsteigend, dann umschalten), die übrigen Spalten nur innerhalb
/// der Page; sichtbare Spalten und ihre Reihenfolge werden als Setting gespeichert („Name“ bleibt immer sichtbar).
/// </summary>
public sealed class GalleryListColumnsTests
{
    [Theory]
    [InlineData(GalleryListColumnKey.Name, GallerySort.ModifiedDesc, GallerySort.NameAsc)]
    [InlineData(GalleryListColumnKey.Name, GallerySort.NameAsc, GallerySort.NameDesc)]
    [InlineData(GalleryListColumnKey.Name, GallerySort.NameDesc, GallerySort.NameAsc)]
    [InlineData(GalleryListColumnKey.Modified, GallerySort.NameAsc, GallerySort.ModifiedAsc)]
    [InlineData(GalleryListColumnKey.Modified, GallerySort.ModifiedAsc, GallerySort.ModifiedDesc)]
    [InlineData(GalleryListColumnKey.Size, GallerySort.SizeDesc, GallerySort.SizeAsc)]
    public void NextSort_beginnt_aufsteigend_und_schaltet_bei_derselben_Spalte_um(GalleryListColumnKey key, GallerySort current, GallerySort expected)
        => GalleryListColumns.NextSort(key, current).Should().Be(expected);

    [Theory]
    [InlineData(GalleryListColumnKey.Folder)]
    [InlineData(GalleryListColumnKey.Type)]
    [InlineData(GalleryListColumnKey.Rating)]
    [InlineData(GalleryListColumnKey.Favorite)]
    [InlineData(GalleryListColumnKey.Tags)]
    [InlineData(GalleryListColumnKey.Status)]
    public void Spalten_ohne_DB_Sortierung_sortieren_nur_innerhalb_der_Page(GalleryListColumnKey key)
    {
        GalleryListColumns.Get(key).SortsServerSide.Should().BeFalse();
        GalleryListColumns.NextSort(key, GallerySort.ModifiedDesc).Should().BeNull();
    }

    [Fact]
    public void ForSort_liefert_Spalte_und_Richtung_fuer_den_Sortierpfeil()
    {
        GalleryListColumns.ForSort(GallerySort.NameDesc).Should().Be((GalleryListColumnKey.Name, true));
        GalleryListColumns.ForSort(GallerySort.ModifiedAsc).Should().Be((GalleryListColumnKey.Modified, false));
        GalleryListColumns.ForSort(GallerySort.SizeDesc).Should().Be((GalleryListColumnKey.Size, true));
    }

    [Fact]
    public void Jede_GallerySort_hat_eine_Spalte()
    {
        foreach (var sort in Enum.GetValues<GallerySort>())
        {
            GalleryListColumns.ForSort(sort).Should().NotBeNull($"{sort} braucht einen Sortierpfeil in der Kopfzeile");
        }
    }

    [Fact]
    public void Standard_zeigt_alle_Spalten_in_Definitionsreihenfolge_inklusive_Status_Platzhalter()
    {
        var settings = GalleryListColumnSettings.Default;
        settings.Visible.Should().Equal(GalleryListColumns.All.Select(c => c.Key));
        settings.Visible.Should().Contain(GalleryListColumnKey.Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_ohne_Wert_ergibt_Standard(string? value)
        => GalleryListColumnSettings.Parse(value).Serialize().Should().Be(GalleryListColumnSettings.Default.Serialize());

    [Fact]
    public void Parse_uebergeht_Unbekanntes_und_Duplikate_und_haelt_Name_sichtbar()
    {
        var settings = GalleryListColumnSettings.Parse("size, Gibtsnicht, tags, Size,  rating");
        settings.Visible.Should().Equal(GalleryListColumnKey.Name, GalleryListColumnKey.Size, GalleryListColumnKey.Tags, GalleryListColumnKey.Rating);
    }

    [Fact]
    public void Serialize_und_Parse_sind_umkehrbar()
    {
        var settings = GalleryListColumnSettings.Parse("Tags,Name,Modified");
        settings.Serialize().Should().Be("Tags,Name,Modified");
        GalleryListColumnSettings.Parse(settings.Serialize()).Visible.Should().Equal(settings.Visible);
    }

    [Fact]
    public void WithVisibility_blendet_aus_und_haengt_eingeblendete_hinten_an()
    {
        var settings = GalleryListColumnSettings.Default
            .WithVisibility(GalleryListColumnKey.Folder, false)
            .WithVisibility(GalleryListColumnKey.Status, false);
        settings.IsVisible(GalleryListColumnKey.Folder).Should().BeFalse();
        settings.IsVisible(GalleryListColumnKey.Status).Should().BeFalse();

        var again = settings.WithVisibility(GalleryListColumnKey.Folder, true);
        again.Visible[^1].Should().Be(GalleryListColumnKey.Folder);
    }

    [Fact]
    public void Name_laesst_sich_nicht_ausblenden()
        => GalleryListColumnSettings.Default.WithVisibility(GalleryListColumnKey.Name, false)
            .IsVisible(GalleryListColumnKey.Name).Should().BeTrue();

    [Fact]
    public void WithOrder_uebernimmt_die_gezogene_Reihenfolge_und_ignoriert_ausgeblendete()
    {
        var settings = GalleryListColumnSettings.Parse("Name,Modified,Size,Tags");
        var reordered = settings.WithOrder([GalleryListColumnKey.Tags, GalleryListColumnKey.Name, GalleryListColumnKey.Folder]);
        reordered.Visible.Should().Equal(GalleryListColumnKey.Tags, GalleryListColumnKey.Name, GalleryListColumnKey.Modified, GalleryListColumnKey.Size);
    }
}
