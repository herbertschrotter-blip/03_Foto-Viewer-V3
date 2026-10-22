using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Services.Stories;
using Lumora.Shell;
using Lumora.Stories;

namespace Lumora.Wpf.Tests.Stories;

/// <summary>
/// Abschnitte nach Tageszeit, Einstellungen › Storys (speichern sofort, Zu Hause, Werte außerhalb der Liste), Statusleiste
/// „Storys aktuell“, Zähler „n neu“ und Medien-Hilfen (ADR-0060).
/// </summary>
public sealed class StoryDaySectionsAndSettingsTests
{
    private static (int, DateTime?) At(int id, int hour) => (id, new DateTime(2022, 10, 21, hour, 15, 0));

    [Fact]
    public void Tageszeiten_teilen_einen_Tag_in_Abschnitte()
    {
        var sections = StoryDaySections.Split([At(1, 9), At(2, 10), At(3, 13), At(4, 19), At(5, 21)]);

        sections.Select(s => s.Label).Should().Equal("Vormittag", "Nachmittag", "Abend");
        sections.Select(s => s.FileIds.Count).Should().Equal(2, 1, 2);
    }

    [Fact]
    public void Eine_Tageszeit_ergibt_keine_Abschnitte_ohne_Uhrzeit_bleibt_beim_vorigen()
    {
        StoryDaySections.Split([At(1, 9), At(2, 11)]).Should().BeEmpty();
        var sections = StoryDaySections.Split([(1, (DateTime?)null), At(2, 9), At(3, 20), (4, null)]);
        sections.Select(s => s.Label).Should().Equal("Vormittag", "Abend");
        sections[0].FileIds.Should().Equal(1, 2);
        sections[1].FileIds.Should().Equal(3, 4);
    }

    [Theory]
    [InlineData(2, "Nacht")]
    [InlineData(5, "Vormittag")]
    [InlineData(12, "Nachmittag")]
    [InlineData(17, "Abend")]
    public void Grenzen_der_Tageszeiten(int hour, string label) => StoryDaySections.Label(hour).Should().Be(label);

    [Theory]
    [InlineData(42_000, "0:42")]
    [InlineData(72_000, "1:12")]
    [InlineData(3_725_000, "1:02:05")]
    public void Videodauer(int ms, string text) => StoryMediaInfo.FormatDuration(TimeSpan.FromMilliseconds(ms)).Should().Be(text);

    [Fact]
    public void Ortszeit_mit_eigenem_Abstand()
        => StoryMediaLoader.LocalTime(new DateTime(2025, 7, 12, 10, 0, 0, DateTimeKind.Utc), 120).Should().Be(new DateTime(2025, 7, 12, 12, 0, 0));

    [Fact]
    public async Task Einstellungen_laden_speichern_jede_Aenderung_und_behalten_Zu_Hause()
    {
        var store = new MemorySettingsStore(new StorySettings(PauseHours: 9, MinFiles: 12, HighlightShare: 0.15, Home: new StoryHomeSetting(42)));
        var vm = new StorySettingsViewModel(store, NullLogger<StorySettingsViewModel>.Instance);

        await vm.LoadAsync();
        store.Saved.Should().BeEmpty("Laden speichert nicht");
        vm.SelectedPause.Label.Should().Be("9 Stunden", "Wert außerhalb der Liste wird ergänzt");
        vm.IsHomeAutomatic.Should().BeFalse();
        vm.PersonsHint.Should().Contain("ab 30 Fotos");

        vm.PersonsEnabled = false;
        vm.SelectedMinFiles = vm.MinFilesOptions.Single(o => o.Value == 20);
        vm.SelectedShare = vm.ShareOptions.Single(o => o.Value == 0.30);
        await Task.Delay(50);

        store.Saved.Should().HaveCount(3);
        var last = store.Saved[^1];
        last.PersonsEnabled.Should().BeFalse();
        last.MinFiles.Should().Be(20);
        last.HighlightShare.Should().Be(0.30);
        last.PauseHours.Should().Be(9);
        last.Home!.PlaceId.Should().Be(42, "Zu Hause bleibt beim Speichern anderer Werte");

        await vm.SetHomeAutomaticAsync();
        store.Saved[^1].HomeOrAutomatic.IsAutomatic.Should().BeTrue();
        vm.IsHomeAutomatic.Should().BeTrue();
        vm.HomeText.Should().StartWith("Automatisch");
    }

    [Fact]
    public void Statusleiste_meldet_die_Story_Erkennung()
    {
        StatusBarViewModel.DescribeStoryIndex(new StoryIndexStatus(true, "Storys werden berechnet …")).Should().Be("Storys werden berechnet …");
        StatusBarViewModel.DescribeStoryIndex(StoryIndexStatus.Idle).Should().Be("Storys aktuell");
        StatusBarViewModel.DescribeStoryIndex(new StoryIndexStatus(false, " ")).Should().BeNull();
    }

    [Fact]
    public void Zaehler_neu_zeigt_Text_nur_mit_neuen()
    {
        var badge = new StoriesNavigationBadge();
        badge.Text.Should().BeEmpty();
        badge.NewCount = 3;
        badge.Text.Should().Be("3 neu");
        badge.HasNew.Should().BeTrue();
        badge.NewCount = 0;
        badge.HasNew.Should().BeFalse();
    }

    private sealed class MemorySettingsStore(StorySettings initial) : IStorySettingsStore
    {
        public List<StorySettings> Saved { get; } = [];

        public Task<StorySettings> LoadAsync(CancellationToken ct = default) => Task.FromResult(initial.Normalize());

        public Task SaveAsync(StorySettings settings, CancellationToken ct = default)
        {
            Saved.Add(settings.Normalize());
            return Task.CompletedTask;
        }
    }
}
