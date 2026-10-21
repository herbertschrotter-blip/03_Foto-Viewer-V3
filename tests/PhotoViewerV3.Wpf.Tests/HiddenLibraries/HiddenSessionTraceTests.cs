using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PhotoViewerV3.Services.Database;
using PhotoViewerV3.Services.HiddenLibraries;
using PhotoViewerV3.Services.Infrastructure;
using PhotoViewerV3.Services.Library;
using PhotoViewerV3.Services.Scanning;
using PhotoViewerV3.Services.Thumbnails;
using PhotoViewerV3.Shell.HiddenLibraries;
using PhotoViewerV3.Shell.Smoke;
using PhotoViewerV3.Wpf.Tests.Fixtures;
using Serilog.Extensions.Logging;

namespace PhotoViewerV3.Wpf.Tests.HiddenLibraries;

/// <summary>
/// ADR-0049, Spurenfreiheit: eine versteckte Sitzung mit der echten App-Zusammenstellung (<c>App.ConfigureServices</c>,
/// Container der versteckten Bibliothek, Logger im Container) — Katalog, Scan, Vorschaubilder. Danach enthält der
/// AppData-Testordner (wie PHOTOVIEWERV3_APPDATA) weder Bibliotheksname noch Pfad noch Dateinamen; alles liegt in
/// <c>.lumora</c>. Seriell (UI-Sammlung), weil die Dienste den globalen Messenger benutzen.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class HiddenSessionTraceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PhotoViewerV3.Wpf.Tests", "trace-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        HiddenSessionPrivacy.IsHiddenSessionActive = false;
        if (Directory.Exists(_root))
        {
            foreach (var dir in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories))
            {
                new DirectoryInfo(dir).Attributes = FileAttributes.Directory;
            }

            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Hidden_session_leaves_no_trace_in_the_app_data_folder()
    {
        var token = Guid.NewGuid().ToString("N")[..10];
        var appData = Directory.CreateDirectory(Path.Combine(_root, "appdata")).FullName; // = PHOTOVIEWERV3_APPDATA
        var library = Directory.CreateDirectory(Path.Combine(_root, "container", "Tresor" + token)).FullName;
        var name = "Versteckt" + token;
        var fileNames = new[] { $"geheim{token}-1.jpg", $"geheim{token}-2.jpg" };
        foreach (var fileName in fileNames)
        {
            TestImages.SaveJpeg(Path.Combine(library, fileName), TestImages.LeftRedRightBlue(640, 480));
        }

        new HiddenLibraryStore(iterations: 1_000).Create(library, name, "kennwort-1");
        var hiddenPaths = HiddenLibraryStore.GetCatalogPaths(library);
        hiddenPaths.EnsureDirectoriesExist();
        var sessions = new CatalogSessionManager(
            new HiddenModeOptions(true, appData, appData, UseUiHooks: false),
            new HiddenLibraryStore(),
            new FixedDriveRoots(),
            new FakeContainers(),
            TimeProvider.System,
            NullLogger.Instance);

        var hiddenLogger = CatalogLoggers.CreateHidden(hiddenPaths.LogsRoot);
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(new SerilogLoggerProvider(hiddenLogger)));
        App.ConfigureServices(services, hiddenPaths, new CatalogSessionSetup(sessions, IsHidden: true, appData));

        await using (var provider = services.BuildServiceProvider())
        {
            provider.GetRequiredService<ICatalogContext>().IsHidden.Should().BeTrue();
            await provider.GetRequiredService<DatabaseInitializer>().InitializeAsync();
            var libraries = provider.GetRequiredService<ILibraryService>();
            var created = await libraries.CreateAsync(name, library);
            await libraries.SetActiveAsync(created.Id);
            var scan = await provider.GetRequiredService<IScanService>().ScanAsync(created.Id, ScanMode.Fresh, null, CancellationToken.None);
            scan.Added.Should().Be(2);

            var thumbnails = provider.GetRequiredService<IThumbnailService>();
            var ready = new TaskCompletionSource();
            var count = 0;
            thumbnails.ThumbnailReady += _ =>
            {
                if (Interlocked.Increment(ref count) == 2)
                {
                    ready.TrySetResult();
                }
            };
            var ids = new Dictionary<string, int>();
            using (var connection = provider.GetRequiredService<ILibraryContextFactory>().OpenSqliteConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT Id, Name FROM Files";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    ids[reader.GetString(1)] = reader.GetInt32(0);
                }
            }

            var requests = fileNames.Select(f => new ThumbnailRequest(created.Id, ids[f], Path.Combine(library, f), Domain.Enums.MediaType.Image, File.GetLastWriteTimeUtc(Path.Combine(library, f)))).ToList();
            await thumbnails.RequestThumbnailsAsync(requests);
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(60));
        }

        hiddenLogger.Dispose();

        var catalog = HiddenLibraryStore.GetCatalogFolder(library);
        File.Exists(Path.Combine(catalog, "library.db")).Should().BeTrue();
        Directory.EnumerateFiles(Path.Combine(catalog, "thumbs"), "*.jpg", SearchOption.AllDirectories).Should().HaveCount(2);
        Directory.EnumerateFiles(Path.Combine(catalog, "logs")).Should().NotBeEmpty("Protokoll der versteckten Sitzung im Container");

        var needles = new List<string> { name, "Tresor" + token, library, token };
        needles.AddRange(fileNames);
        SmokeScenario.FindTraces(appData, needles).Should().BeEmpty("keine Spur im AppData-Testordner");
        SmokeScenario.FindTraces(catalog, [name]).Should().NotBeEmpty("Gegenprobe: die Suche findet den Namen im Katalog");
    }
}
