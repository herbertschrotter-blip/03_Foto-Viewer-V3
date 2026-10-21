using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using PhotoViewerV3.Domain.Enums;
using PhotoViewerV3.Gallery.ListView;
using PhotoViewerV3.Lightbox.Faces;

namespace PhotoViewerV3.Wpf.Tests.Persons;

/// <summary>
/// Taste F dauerhaft gemerkt (0.2.39): jede Taste schreibt <c>ui.lightbox.showFaces</c>, ein neues Lightbox-ViewModel
/// stellt den Zustand wieder her, beim Blättern bleibt er stehen; ignorierte Gesichter bekommen keinen Rahmen.
/// </summary>
public sealed class LightboxFacesPersistenceTests
{
    private const int Photo = 7;
    private const int OtherPhoto = 8;
    private readonly FakeFaceRecognitionService _service = new();
    private readonly FakeSettingsStore _settings = new();

    public LightboxFacesPersistenceTests()
    {
        var anna = _service.AddPerson("Anna", 1);
        _service.AddFace(Photo, FaceState.Confirmed, personId: anna, x: 0.1f);
        _service.AddFace(Photo, FaceState.Ignored, x: 0.5f);
        _service.AddFace(OtherPhoto, FaceState.Unconfirmed, x: 0.3f);
        _service.AddFace(OtherPhoto, FaceState.Ignored, x: 0.7f);
    }

    private LightboxFacesViewModel Create(IGalleryViewSettingsStore? settings = null)
        => new(_service.AsService(), new FakePersonDialogs(), messenger: new WeakReferenceMessenger(), dispatcher: Dispatcher.CurrentDispatcher, settings: settings ?? _settings);

    [Fact]
    public async Task Taste_F_speichert_den_Zustand()
    {
        var vm = Create();
        await vm.SetFileAsync(Photo, isImage: true);

        await vm.ToggleAsync();
        _settings.Values[LightboxFacesViewModel.ShowFacesKey].Should().Be("true");

        await vm.ToggleAsync();
        _settings.Values[LightboxFacesViewModel.ShowFacesKey].Should().Be("false");
    }

    [Fact]
    public async Task Neue_Lightbox_stellt_eingeschaltete_Rahmen_wieder_her_und_behaelt_sie_beim_Blaettern()
    {
        _settings.Values[LightboxFacesViewModel.ShowFacesKey] = "true";
        var vm = Create();
        await vm.RestoreAsync();
        vm.IsVisible.Should().BeTrue();

        await vm.SetFileAsync(Photo, isImage: true);
        vm.Boxes.Select(b => b.Label).Should().Equal(["Anna"], "ignorierte Gesichter bekommen keinen Rahmen");

        await vm.SetFileAsync(OtherPhoto, isImage: true);
        vm.IsVisible.Should().BeTrue("der Zustand bleibt beim Blättern");
        vm.Boxes.Select(b => b.Label).Should().Equal(["Unbekannt"]);
        _settings.Writes.Should().Be(0, "Wiederherstellen und Blättern schreiben nichts");
    }

    [Fact]
    public async Task Wiederherstellen_nach_dem_Laden_der_Datei_zeigt_die_Rahmen_sofort()
    {
        _settings.Values[LightboxFacesViewModel.ShowFacesKey] = "true";
        var vm = Create();
        await vm.SetFileAsync(Photo, isImage: true);
        vm.Boxes.Should().BeEmpty();

        await vm.RestoreAsync();

        vm.ShowBoxes.Should().BeTrue();
        vm.Boxes.Should().ContainSingle();
    }

    [Fact]
    public async Task Ausgeschaltet_gemerkt_oder_ohne_Eintrag_bleiben_die_Rahmen_aus()
    {
        var vm = Create();
        await vm.RestoreAsync();
        vm.IsVisible.Should().BeFalse("ohne Eintrag gilt aus");

        _settings.Values[LightboxFacesViewModel.ShowFacesKey] = "false";
        var second = Create();
        await second.RestoreAsync();
        second.IsVisible.Should().BeFalse();
    }

    [Fact]
    public async Task Taste_F_vor_dem_Wiederherstellen_gewinnt()
    {
        var pending = new TaskCompletionSource<string?>();
        var slow = new FakeSettingsStore { PendingGet = pending.Task };
        var vm = Create(slow);
        await vm.SetFileAsync(Photo, isImage: true);
        var restore = vm.RestoreAsync();

        await vm.ToggleAsync(); // an
        await vm.ToggleAsync(); // wieder aus
        pending.SetResult("true");
        await restore;

        vm.IsVisible.Should().BeFalse("die Taste des Benutzers überschreibt der gemerkte Zustand nicht");
        slow.Values[LightboxFacesViewModel.ShowFacesKey].Should().Be("false");
    }

    [Fact]
    public async Task Ohne_Zuordnungsdienst_wird_nichts_wiederhergestellt()
    {
        _settings.Values[LightboxFacesViewModel.ShowFacesKey] = "true";
        var vm = new LightboxFacesViewModel(null, dispatcher: Dispatcher.CurrentDispatcher, settings: _settings);
        await vm.RestoreAsync();
        vm.IsVisible.Should().BeFalse();
    }

    [Fact]
    public async Task Zustand_ueberlebt_in_der_Settings_Tabelle_einer_migrierten_DB()
    {
        var root = Path.Combine(Path.GetTempPath(), "PhotoViewerV3.Wpf.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new TempPaths(root);
            paths.EnsureDirectoriesExist();
            var factory = new PhotoViewerV3.Services.Database.LibraryContextFactory(paths, new PhotoViewerV3.Services.Database.SqlitePragmaInterceptor());
            await new PhotoViewerV3.Services.Database.DatabaseInitializer(factory, Microsoft.Extensions.Logging.Abstractions.NullLogger<PhotoViewerV3.Services.Database.DatabaseInitializer>.Instance).InitializeAsync();
            var store = new GalleryViewSettingsStore(factory, Microsoft.Extensions.Logging.Abstractions.NullLogger<GalleryViewSettingsStore>.Instance);

            var first = Create(store);
            await first.SetFileAsync(Photo, isImage: true);
            await first.ToggleAsync();
            first.Dispose();

            var reopened = Create(store);
            await reopened.SetFileAsync(Photo, isImage: true);
            await reopened.RestoreAsync();
            reopened.IsVisible.Should().BeTrue();
            reopened.Boxes.Should().ContainSingle();
            (await store.GetAsync(LightboxFacesViewModel.ShowFacesKey)).Should().Be("true");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class TempPaths(string root) : PhotoViewerV3.Services.Infrastructure.IAppDataPaths
    {
        public string AppDataRoot => root;
        public string LibraryDatabasePath => Path.Combine(root, "library.db");
        public string ThumbnailsRoot => Path.Combine(root, "thumbs");
        public string LogsRoot => Path.Combine(root, "logs");
        public string GetLibraryThumbnailsPath(long libraryId) => Path.Combine(ThumbnailsRoot, libraryId.ToString());
        public void EnsureDirectoriesExist() => Directory.CreateDirectory(root);
    }

    private sealed class FakeSettingsStore : IGalleryViewSettingsStore
    {
        public Dictionary<string, string?> Values { get; } = new();

        public int Writes { get; private set; }

        public Task<string?>? PendingGet { get; init; }

        public Task<string?> GetAsync(string key, CancellationToken ct = default)
            => PendingGet ?? Task.FromResult(Values.TryGetValue(key, out var value) ? value : null);

        public Task SetAsync(string key, string? value, CancellationToken ct = default)
        {
            Values[key] = value;
            Writes++;
            return Task.CompletedTask;
        }
    }
}
