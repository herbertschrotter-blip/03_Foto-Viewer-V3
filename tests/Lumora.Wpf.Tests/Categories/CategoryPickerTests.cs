using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Lumora.Gallery.Categories;
using Lumora.Gallery.Data;
using Lumora.Gallery.ViewModels;
using Lumora.Services.Categories;
using Lumora.Wpf.Tests.Fixtures;

namespace Lumora.Wpf.Tests.Categories;

/// <summary>
/// Kategorie-Zuweisen per Tastatur (Strg+K, ADR-0027, 0.2.23): Ausgangszustand je Kategorie, nur Änderungen im
/// Ergebnis, echtes Dialogfenster (Fokus, Enter/Esc), Tastenkürzel ohne Doppelbelegung in Galerie und Lightbox sowie
/// die Logik der Kategorie-Sortierung (Kopfzeilenklick).
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class CategoryPickerTests
{
    private static readonly CategoryInfo Xx = new(1, "XX", 0, [new(11, 1, "N", 0), new(12, 1, "NN", 1)]);
    private static readonly CategoryInfo Projekt = new(2, "Projekt", 1, [new(21, 2, "Nord", 0)]);

    private static CategoryPickRequest Request(params (int FileId, int? Xx, int? Projekt)[] files)
    {
        var values = new Dictionary<int, IReadOnlyDictionary<int, int>>();
        foreach (var (fileId, xx, projekt) in files)
        {
            var map = new Dictionary<int, int>();
            if (xx is { } x)
            {
                map[Xx.Id] = x;
            }

            if (projekt is { } p)
            {
                map[Projekt.Id] = p;
            }

            if (map.Count > 0)
            {
                values[fileId] = map;
            }
        }

        return new CategoryPickRequest([Xx, Projekt], values, files.Select(f => f.FileId).ToList());
    }

    [Fact]
    public void Ausgangszustand_gemeinsamer_Wert_kein_Wert_oder_gemischt()
    {
        var items = CategoryPickLogic.BuildItems(Request((1, 11, null), (2, 11, null)));
        items.Select(i => i.Name).Should().Equal("XX", "Projekt");
        items[0].Initial.ValueId.Should().Be(11, "beide Dateien haben N");
        items[0].Options.Select(o => o.Label).Should().Equal("N", "NN", CategoryPickLogic.NoneLabel);
        items[1].Initial.IsNone.Should().BeTrue("keine Datei hat einen Projekt-Wert");
        items[1].Hint.Should().BeEmpty();

        var mixed = CategoryPickLogic.BuildItems(Request((1, 11, 21), (2, 12, null), (3, null, null)));
        mixed[0].Initial.KeepsMixed.Should().BeTrue("N, NN und ohne Wert");
        mixed[0].Options[0].Label.Should().Be(CategoryPickLogic.MixedLabel);
        mixed[0].Hint.Should().Be("2 von 3 mit Wert");
        mixed[1].Initial.KeepsMixed.Should().BeTrue("ein Teil hat Nord, ein Teil keinen Wert");
    }

    [Fact]
    public void Unbekannte_Werte_zaehlen_als_kein_Wert()
    {
        var items = CategoryPickLogic.BuildItems(Request((1, 999, null)));
        items[0].Initial.IsNone.Should().BeTrue("ein Wert, den die Kategorie nicht (mehr) kennt, wird ignoriert");
    }

    [Fact]
    public void Ergebnis_enthaelt_nur_geaenderte_Kategorien_gemischt_aendert_nichts()
    {
        var items = CategoryPickLogic.BuildItems(Request((1, 11, 21), (2, 12, null)));
        CategoryPickLogic.ComputeResult(items).IsEmpty.Should().BeTrue("unverändert");

        items[0].Selected = items[0].Find("NN")!;
        items[1].Selected = items[1].Find(CategoryPickLogic.NoneLabel)!;
        CategoryPickLogic.ComputeResult(items).Changes.Should().Equal(new CategoryValueChange(Xx.Id, 12), new CategoryValueChange(Projekt.Id, null));

        items[0].Selected = items[0].Find(CategoryPickLogic.MixedLabel)!;
        CategoryPickLogic.ComputeResult(items).Changes.Should().Equal(new CategoryValueChange(Projekt.Id, null));
    }

    [Fact]
    public void Kein_Wert_bei_einer_Datei_mit_Wert_entfernt_ihn()
    {
        var items = CategoryPickLogic.BuildItems(Request((1, 11, null)));
        items[0].Selected = items[0].Find(CategoryPickLogic.NoneLabel)!;
        CategoryPickLogic.ComputeResult(items).Changes.Should().Equal(new CategoryValueChange(Xx.Id, null));
    }

    [Theory]
    [InlineData(Key.Enter, true)]
    [InlineData(Key.Escape, false)]
    public void Dialog_fokussiert_die_erste_Auswahl_Enter_uebernimmt_Esc_bricht_ab(Key key, bool accepted)
    {
        var (result, focusedCombo, itemCount) = TestUiThread.Run(() =>
        {
            var window = new CategoryPickerDialog(Request((1, 11, null))) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
            var focused = false;
            window.Loaded += (_, _) => window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
            {
                var logical = FocusManager.GetFocusedElement(window);
                focused = logical is ComboBox; // logischer Fokus: unabhängig davon, ob das Testfenster im Vordergrund ist
                window.Items[0].Selected = window.Items[0].Find("NN")!;
                var target = logical ?? window;
                var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, key) { RoutedEvent = Keyboard.KeyDownEvent };
                target.RaiseEvent(args);
                if (window.IsVisible)
                {
                    window.Close(); // Sicherheitsnetz: Taste nicht verarbeitet → Test scheitert unten statt zu hängen
                }
            });
            _ = window.ShowDialog();
            return (window.Result, focused, window.Items.Count);
        }, TimeSpan.FromSeconds(20));

        itemCount.Should().Be(2);
        focusedCombo.Should().BeTrue("Tastaturbedienung: die erste Kategorie hat den Fokus");
        if (accepted)
        {
            result.Should().NotBeNull();
            result!.Changes.Should().Equal(new CategoryValueChange(Xx.Id, 12));
        }
        else
        {
            result.Should().BeNull("Esc bricht ab");
        }
    }

    [Fact]
    public void Kategorie_Sortierung_erst_aufsteigend_dann_umgekehrt()
    {
        var first = GalleryCategorySortLogic.Next(null, 5);
        first.Should().Be(new GalleryCategorySort(5, false));
        GalleryCategorySortLogic.Next(first, 5).Should().Be(new GalleryCategorySort(5, true));
        GalleryCategorySortLogic.Next(new GalleryCategorySort(5, true), 5).Should().Be(new GalleryCategorySort(5, false));
        GalleryCategorySortLogic.Next(new GalleryCategorySort(5, true), 6).Should().Be(new GalleryCategorySort(6, false), "andere Spalte beginnt aufsteigend");
        GalleryCategorySortLogic.Label("XX", false).Should().Be("XX (Kategorie) ↑");
        GalleryCategorySortLogic.Label("XX", true).Should().Be("XX (Kategorie) ↓");
    }

    [Fact]
    public void Strg_K_ist_in_Galerie_und_Lightbox_belegt_ohne_ein_Kuerzel_doppelt_zu_vergeben()
    {
        var root = FindRepoRoot();
        var galleryXaml = File.ReadAllText(Path.Combine(root, "src", "Lumora.Wpf", "Gallery", "Views", "GalleryView.xaml"));
        var listXaml = File.ReadAllText(Path.Combine(root, "src", "Lumora.Wpf", "Gallery", "ListView", "GalleryListView.xaml"));
        var bindings = Regex.Matches(galleryXaml + listXaml, "<KeyBinding(?: Modifiers=\"(?<m>\\w+)\")? Key=\"(?<k>\\w+)\" Command=\"\\{Binding (?<c>\\w+)\\}\"")
            .Select(m => (Gesture: $"{m.Groups["m"].Value}+{m.Groups["k"].Value}", Command: m.Groups["c"].Value))
            .ToList();
        bindings.Should().Contain(("Ctrl+K", "EditSelectionCategoriesCommand"));
        bindings.Should().Contain(("Ctrl+T", "EditSelectionTagsCommand"), "Strg+T bleibt Tags");
        bindings.Where(b => b.Gesture == "Ctrl+K").Should().ContainSingle();
        var galleryGestures = Regex.Matches(galleryXaml, "<KeyBinding(?: Modifiers=\"(\\w+)\")? Key=\"(\\w+)\"").Select(m => $"{m.Groups[1].Value}+{m.Groups[2].Value}").ToList();
        galleryGestures.Should().OnlyHaveUniqueItems("kein Kürzel der Galerie ist doppelt belegt");

        var lightbox = File.ReadAllText(Path.Combine(root, "src", "Lumora.Wpf", "Lightbox", "LightboxWindow.xaml.cs"));
        var cases = Regex.Matches(lightbox, "case Key\\.(\\w+)( when ctrl)?:").Select(m => m.Groups[1].Value + m.Groups[2].Value).ToList();
        cases.Should().Contain("K when ctrl").And.Contain("T when ctrl");
        cases.Should().OnlyHaveUniqueItems("kein Kürzel der Lightbox ist doppelt belegt");
        Regex.IsMatch(lightbox, "case Key\\.K when ctrl:\\s*_ = _viewModel\\.EditCategoriesCommand").Should().BeTrue("Strg+K öffnet in der Lightbox die Kategorie-Auswahl");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Lumora.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repo-Wurzel (Lumora.slnx) nicht gefunden");
    }
}
