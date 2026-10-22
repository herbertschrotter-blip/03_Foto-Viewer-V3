using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Lumora.Domain.Enums;
using Lumora.Shell;
using Lumora.Stories;
using Lumora.Wpf.Tests.Fixtures;
using Wpf.Ui.Appearance;

namespace Lumora.Wpf.Tests.Stories;

/// <summary>
/// Echte Seite „Storys“ im Fenster (Test-UI-Thread) mit Fake-Daten (ADR-0060): Übersicht und Story-Ansicht laden in hell
/// und dunkel ohne Binding-Fehler, Zeilen sind virtualisiert (hunderte Storys), Karten und Foto-Kacheln sind im
/// Automation-Baum Blätter, sichtbare Reihen fordern Bilder an; Navigation, Reiter und Galerie-Menü existieren.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class StoriesPageUiTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void Uebersicht_und_Story_laden_virtualisiert_ohne_Binding_Fehler(ApplicationTheme theme)
    {
        TestUiThread.Run(
            () =>
            {
                var previous = ApplicationThemeManager.GetAppTheme();
                ApplicationThemeManager.Apply(theme);
                var kit = new StoryTestKit();
                kit.Stories.Memories.Add(new Lumora.Services.Stories.StoryMemory("R:D:2022-10-21", Lumora.Services.Stories.StoryMemoryKind.OnThisDay, 3,
                    new DateOnly(2022, 10, 21), "Heute vor 3 Jahren", "Freitag, 21. Oktober 2022", "Wien", "48 Fotos", 48, 0, 701, [701, 702, 703, 704, 705], null, false));
                kit.Stories.Add(FakeStoryService.Card(1, StoryKind.Trip, "12.–19. Juli 2025", pinned: true, seen: false, days: 6), dayCount: 6, perDay: 12);
                kit.Stories.Add(FakeStoryService.Card(2, StoryKind.Person, "Anna · 2019", year: 2019, subtitle: "312 Fotos mit Anna", personId: 1));
                for (var i = 0; i < 300; i++)
                {
                    kit.Stories.Add(FakeStoryService.Card(10 + i, StoryKind.Event, $"Ereignis {i}", year: 2000 + (i % 25)), perDay: 3);
                }

                var vm = kit.CreateOverview();
                var page = new StoriesPage(vm);
                using var errors = new BindingErrors();
                var window = new Window
                {
                    Width = 1280,
                    Height = 800,
                    WindowStyle = WindowStyle.None,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -5000,
                    Top = -5000,
                    Content = page,
                };
                try
                {
                    window.Show();
                    PumpUntil(() => !vm.IsLoading && vm.Rows.Count > 0);
                    window.UpdateLayout();
                    DoEvents();

                    // Übersicht: Banner zuerst, Zeilen virtualisiert, sichtbare Reihen laden Bilder
                    vm.Rows[0].Should().BeOfType<OverviewMemoryRow>();
                    var kindButtons = FindAll<StorySelectButton>(page).Where(b => b.DataContext is StoryKindOption).ToList();
                    kindButtons.Should().HaveCount(5);
                    kindButtons.Where(b => b.IsChecked == true).Select(b => ((StoryKindOption)b.DataContext).Filter).Should().Equal(StoryKindFilter.All);
                    kindButtons[0].Foreground.Should().NotBe(kindButtons[1].Foreground, "gewählter Knopf in Akzentfarbe");
                    vm.SelectKind(vm.KindOptions.Single(o => o.Filter == StoryKindFilter.Events));
                    window.UpdateLayout();
                    DoEvents();
                    var list = (ItemsControl)page.FindName("OverviewList");
                    list.Items.Count.Should().BeGreaterThan(70, "300 Ereignisse in Reihen");
                    var realized = Enumerable.Range(0, list.Items.Count).Count(i => list.ItemContainerGenerator.ContainerFromIndex(i) is not null);
                    realized.Should().BeGreaterThan(0).And.BeLessThan(20, "Zeilen sind virtualisiert");
                    PumpUntil(() => kit.Images.Requested.Count > 0);
                    kit.Images.Requested.Should().NotContain(30900, "Karten weit unten laden noch keine Bilder");

                    // Karten sind Automation-Blätter mit Namen
                    var cards = FindAll<StoryLeafButton>(list).ToList();
                    cards.Should().NotBeEmpty();
                    foreach (var card in cards.Take(5))
                    {
                        var peer = UIElementAutomationPeer.CreatePeerForElement(card);
                        peer.GetChildren().Should().BeNullOrEmpty("Karte ist ein Blatt");
                        peer.GetName().Should().Contain(":");
                    }

                    // Story öffnen: Titelbild, Leiste, Collage-Kacheln als Blätter
                    _ = vm.OpenStoryAsync(1);
                    PumpUntil(() => vm.CurrentStory is { IsLoading: false, Details: not null });
                    window.UpdateLayout();
                    DoEvents();
                    var storyView = FindAll<StoryView>(page).Single();
                    var rows = (ItemsControl)storyView.FindName("RowsList");
                    rows.Items.Count.Should().BeGreaterThan(5);
                    var tiles = FindAll<StoryLeafButton>(rows).Where(b => b.DataContext is StoryTileViewModel).ToList();
                    tiles.Should().NotBeEmpty();
                    foreach (var tile in tiles.Take(5))
                    {
                        var peer = UIElementAutomationPeer.CreatePeerForElement(tile);
                        peer.GetChildren().Should().BeNullOrEmpty("Foto-Kachel ist ein Blatt");
                        peer.GetName().Should().MatchRegex(@"^\d+\.(jpg|mp4)");
                    }

                    var bar = (Border)storyView.FindName("Bar");
                    bar.IsVisible.Should().BeTrue();
                    vm.CurrentStory!.ShowAllPhotos();
                    window.UpdateLayout();
                    DoEvents();
                    vm.CurrentStory.JumpToDay(vm.CurrentStory.DayChips.Last());
                    window.UpdateLayout();
                    DoEvents();
                    DoEvents();
                    vm.CurrentStory.Back();
                    DoEvents();
                    vm.IsStoryOpen.Should().BeFalse();

                    errors.Messages.Should().BeEmpty("keine Binding-Fehler");
                }
                finally
                {
                    window.Close();
                    if (previous != ApplicationTheme.Unknown)
                    {
                        ApplicationThemeManager.Apply(previous);
                    }
                }

                return 0;
            },
            Timeout);
    }

    [Fact]
    public void Einstellungs_Reiter_Storys_laedt_ohne_Binding_Fehler()
    {
        TestUiThread.Run(
            () =>
            {
                var vm = new StorySettingsViewModel(new NullStore(), Microsoft.Extensions.Logging.Abstractions.NullLogger<StorySettingsViewModel>.Instance);
                var section = new SettingsStoriesSection { DataContext = vm };
                using var errors = new BindingErrors();
                var window = new Window { Width = 1000, Height = 800, WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false, Left = -5000, Top = -5000, WindowStartupLocation = WindowStartupLocation.Manual, Content = section };
                try
                {
                    window.Show();
                    window.UpdateLayout();
                    DoEvents();
                    PumpUntil(() => vm.HomeText.StartsWith("Automatisch", StringComparison.Ordinal));
                    vm.ChooseHomeCommand.Execute(null);
                    DoEvents();
                    errors.Messages.Should().BeEmpty();
                }
                finally
                {
                    window.Close();
                }

                return 0;
            },
            Timeout);
    }

    [Fact]
    public void Galerie_Menue_bietet_Als_Story_und_In_Storys_zeigen()
    {
        TestUiThread.Run(
            () =>
            {
                var kit = new StoryTestKit();
                kit.Stories.Add(FakeStoryService.Card(1, StoryKind.Trip, "12.–19. Juli 2025"));
                var menu = new ContextMenu();
                kit.Bridge.AddMenuItems(menu, [100, 101], 100);
                var headers = menu.Items.OfType<MenuItem>().Select(i => i.Header as string).ToList();
                headers.Should().Contain("Als Story… (2 ausgewählt)").And.Contain("In Storys zeigen");
                headers.Should().NotContain(h => h != null && h.StartsWith("Zur Story", StringComparison.Ordinal));

                kit.Bridge.BeginAdd(1, "12.–19. Juli 2025");
                kit.Navigation.Tags.Should().Equal(NavigationTags.Gallery);
                var second = new ContextMenu();
                kit.Bridge.AddMenuItems(second, [], 100);
                second.Items.OfType<MenuItem>().First().Header.Should().Be("Zur Story „12.–19. Juli 2025“ hinzufügen");

                var show = menu.Items.OfType<MenuItem>().Single(i => (string)i.Header == "In Storys zeigen");
                kit.Bridge.FillStoriesForFileAsync(show, 100).GetAwaiter().GetResult();
                show.Items.OfType<MenuItem>().Single().Header.Should().Be("Reise: 12.–19. Juli 2025");
                return 0;
            },
            Timeout);
    }

    [Fact]
    public void Navigation_Reiter_und_Symbole()
    {
        NavigationTags.Stories.Should().Be("stories");
        NavigationTags.IsSettingsSection(NavigationTags.Stories).Should().BeFalse("eigene Seite in der Leiste");
        NavigationTags.IsSettingsSection(NavigationTags.SettingsStories).Should().BeTrue();
        foreach (var symbol in new[] { "BookOpen24", "Airplane24", "CalendarStar24", "VehicleCar24", "History24", "Person24", "Location24", "Pin16", "EyeOff24", "SlideMultiple24", "Sparkle16" })
        {
            Enum.TryParse<global::Wpf.Ui.Controls.SymbolRegular>(symbol, out _).Should().BeTrue(symbol);
        }

        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Lumora.Wpf", "MainWindow.xaml"));
        xaml.Should().Contain("TargetPageTag=\"stories\"");
        xaml.IndexOf("TargetPageTag=\"albums\"", StringComparison.Ordinal).Should().BeLessThan(xaml.IndexOf("TargetPageTag=\"stories\"", StringComparison.Ordinal), "Storys nach Alben");
        var appearance = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Lumora.Wpf", "Settings", "Sections", "SettingsAppearanceSection.xaml"));
        appearance.Should().NotContain("SettingsSlideshowSection", "Diashow ist nach „Storys“ verschoben");
    }

    private sealed class NullStore : Lumora.Services.Stories.IStorySettingsStore
    {
        public Task<Lumora.Services.Stories.StorySettings> LoadAsync(CancellationToken ct = default) => Task.FromResult(Lumora.Services.Stories.StorySettings.Default);

        public Task SaveAsync(Lumora.Services.Stories.StorySettings settings, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>Sammelt WPF-Binding-Fehler während des Tests.</summary>
    private sealed class BindingErrors : TraceListener
    {
        public BindingErrors()
        {
            PresentationTraceSources.Refresh();
            PresentationTraceSources.DataBindingSource.Listeners.Add(this);
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        }

        public List<string> Messages { get; } = [];

        public override void Write(string? message)
        {
        }

        public override void WriteLine(string? message)
        {
            if (message is not null)
            {
                Messages.Add(message);
            }
        }

        protected override void Dispose(bool disposing)
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(this);
            base.Dispose(disposing);
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Lumora.slnx")))
        {
            dir = dir.Parent;
        }

        return dir!.FullName;
    }

    private static IEnumerable<T> FindAll<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in FindAll<T>(child))
            {
                yield return nested;
            }
        }
    }

    private static void PumpUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            DoEvents();
            Thread.Sleep(5);
        }

        condition().Should().BeTrue("Bedingung innerhalb von 15 s");
    }

    private static void DoEvents()
    {
        var frame = new DispatcherFrame();
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }
}
