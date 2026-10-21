using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PhotoViewerV3.Services;
using PhotoViewerV3.Services.Database;
using PhotoViewerV3.Services.Database.Relocation;
using PhotoViewerV3.Services.Faces.Models;
using PhotoViewerV3.Services.HiddenLibraries;
using PhotoViewerV3.Services.Infrastructure;
using PhotoViewerV3.Services.Library;
using PhotoViewerV3.Services.Media;
using PhotoViewerV3.Services.Scanning;
using PhotoViewerV3.Tests.Fixtures;

namespace PhotoViewerV3.Tests.HiddenLibraries;

/// <summary>
/// ADR-0049: Sitzung einer versteckten Bibliothek in der Services-Schicht — eigener Katalog in <c>.lumora</c>, Kontext
/// „versteckt“, Katalog-Umzug gesperrt, Gesichtsmodelle aus dem normalen Ordner, <c>.lumora</c> nie mitgescannt, nichts
/// im normalen Datenordner.
/// </summary>
public sealed class HiddenCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PhotoViewerV3.Tests", "hidden-catalog-" + Guid.NewGuid().ToString("N"));

    public HiddenCatalogTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var dir in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories))
        {
            new DirectoryInfo(dir).Attributes = FileAttributes.Directory;
        }

        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Normal_session_registers_a_normal_catalog_context_with_cache_and_temp_under_app_data()
    {
        var paths = new TestAppDataPaths(Path.Combine(_root, "appdata"));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IAppDataPaths>(paths);
        services.AddPhotoViewerServices();
        using var provider = services.BuildServiceProvider();

        var context = provider.GetRequiredService<ICatalogContext>();
        context.IsHidden.Should().BeFalse();
        context.DatabasePath.Should().Be(paths.LibraryDatabasePath);
        context.CacheRoot.Should().Be(Path.Combine(paths.AppDataRoot, "cache"));
        context.TempRoot.Should().Be(Path.Combine(paths.AppDataRoot, "temp"));
        provider.GetRequiredService<ICatalogStoragePaths>().Should().BeSameAs(context);
        services.Single(d => d.ServiceType == typeof(IDatabaseRelocationService)).ImplementationType.Should().Be(typeof(DatabaseRelocationService));
    }

    [Fact]
    public async Task Hidden_session_keeps_catalog_scan_and_settings_inside_the_container()
    {
        var normalRoot = Directory.CreateDirectory(Path.Combine(_root, "appdata")).FullName;
        var library = Directory.CreateDirectory(Path.Combine(_root, "container", "Tresor")).FullName;
        new HiddenLibraryStore(iterations: 1_000).Create(library, "Tresor", "kennwort-1");
        foreach (var name in new[] { "geheim-1.jpg", "geheim-2.png", Path.Combine("Urlaub", "geheim-3.jpg") })
        {
            var file = Path.Combine(library, name);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, new byte[512]);
        }

        var hiddenPaths = HiddenLibraryStore.GetCatalogPaths(library);
        hiddenPaths.EnsureDirectoriesExist();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IAppDataPaths>(hiddenPaths);
        services.AddHiddenCatalog(hiddenPaths, normalRoot);
        services.AddPhotoViewerServices();

        await using (var provider = services.BuildServiceProvider())
        {
            provider.GetRequiredService<ICatalogContext>().IsHidden.Should().BeTrue();
            provider.GetRequiredService<ICatalogStoragePaths>().CacheRoot.Should().StartWith(HiddenLibraryStore.GetCatalogFolder(library));
            var relocation = provider.GetRequiredService<IDatabaseRelocationService>();
            relocation.Should().BeOfType<HiddenCatalogRelocationBlocker>();
            (await relocation.CheckTargetAsync(normalRoot)).State.Should().Be(RelocationTargetState.Invalid);
            (await relocation.RelocateAsync(normalRoot, RelocationMode.Move)).Success.Should().BeFalse();
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(IFaceModelProvider.OverrideVariable)))
            {
                provider.GetRequiredService<IFaceModelProvider>().ModelFolder.Should().Be(Path.Combine(normalRoot, "models", "buffalo_l"));
            }

            await provider.GetRequiredService<DatabaseInitializer>().InitializeAsync();
            var libraries = provider.GetRequiredService<ILibraryService>();
            var created = await libraries.CreateAsync("Tresor", library);
            await libraries.SetActiveAsync(created.Id);
            var scan = await provider.GetRequiredService<IScanService>().ScanAsync(created.Id, ScanMode.Fresh, null, CancellationToken.None);
            scan.Outcome.Should().Be(ScanOutcome.Completed);
            scan.Added.Should().Be(3, ".lumora (Katalog, Protokolle) wird nicht mitgescannt");
        }

        File.Exists(Path.Combine(library, ".lumora", "library.db")).Should().BeTrue();
        Directory.EnumerateFileSystemEntries(normalRoot, "*", SearchOption.AllDirectories).Should().BeEmpty("nichts im normalen Datenordner");
    }

    [Fact]
    public void Enumerator_skips_lumora_even_without_hidden_attribute()
    {
        var library = Directory.CreateDirectory(Path.Combine(_root, "lib")).FullName;
        File.WriteAllBytes(Path.Combine(library, "foto.jpg"), new byte[10]);
        var catalog = Directory.CreateDirectory(Path.Combine(library, ".lumora", "thumbs", "1")).FullName;
        File.WriteAllBytes(Path.Combine(catalog, "1_400_still.jpg"), new byte[10]);

        var files = new FolderEnumerator(new MediaFormatRegistry(), NullLogger<FolderEnumerator>.Instance).Enumerate(library).ToList();

        files.Select(f => f.Name).Should().Equal("foto.jpg");
        CatalogFolders.IsCatalogFolder(@"X:\Tresor\.LUMORA\").Should().BeTrue();
        CatalogFolders.IsCatalogFolder(@"X:\Tresor\lumora").Should().BeFalse();
    }
}
