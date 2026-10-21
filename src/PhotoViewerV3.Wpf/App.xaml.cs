using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using PhotoViewerV3.Faces;
using PhotoViewerV3.Folders;
using PhotoViewerV3.Services.Faces.Indexing;
using PhotoViewerV3.Gallery;
using PhotoViewerV3.Services;
using PhotoViewerV3.Services.Database;
using PhotoViewerV3.Services.Infrastructure;
using PhotoViewerV3.Services.Thumbnails;
using PhotoViewerV3.Services.Archive;
using PhotoViewerV3.Services.Watch;
using PhotoViewerV3.Lightbox;
using PhotoViewerV3.Persons;
using PhotoViewerV3.Services.Faces.Assignment;
using PhotoViewerV3.Services.Faces.Crops;
using PhotoViewerV3.Settings;
using PhotoViewerV3.Settings.CategoryManagement;
using PhotoViewerV3.Settings.FaceData;
using PhotoViewerV3.Settings.PersonRelations;
using PhotoViewerV3.Settings.TagManagement;
using PhotoViewerV3.Shell;
using PhotoViewerV3.Sorter;
using PhotoViewerV3.Thumbnails;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

namespace PhotoViewerV3;

/// <summary>
/// Application Bootstrap mit HostBuilder, DI-Container und Serilog gemäß ADR-0001 und ADR-0002.
/// Unbehandelte Ausnahmen (UI-Thread, AppDomain, Tasks) landen immer im Log; im Debug-Build und im
/// Rauchtest werden zusätzlich WPF-Binding-Fehler protokolliert.
/// </summary>
public partial class App : Application
{
    private readonly IHost _host;
    private readonly AppDataPaths _paths;
    private readonly string? _locationError;

    public App()
    {
        // Produktiv %LocalAppData%\PhotoViewerV3; per PHOTOVIEWERV3_APPDATA umlenkbar (Rauchtests mit frischer DB).
        // DB und Thumbnails liegen dort oder im Ordner aus database-location.json (Einstellungen → Speicherorte, ADR-0026).
        AppDataPaths paths;
        try
        {
            paths = AppDataRootOverride.Resolve();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            // Nur für das Protokoll; OnStartup bricht ab, bevor eine DB geöffnet wird (sonst entstünde am Standardort ein leerer Katalog).
            _locationError = ex.Message;
            var root = Environment.GetEnvironmentVariable(AppDataRootOverride.EnvironmentVariable);
            paths = new AppDataPaths(string.IsNullOrWhiteSpace(root) ? AppDataPaths.DefaultAppDataRoot : Path.GetFullPath(root));
        }

        _paths = paths;
        paths.EnsureDirectoriesExist();

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            // EF Core protokolliert sonst jeden SQL-Befehl (tausende Zeilen pro Sitzung, I/O auf den Thumbnail-Workern)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
            .WriteTo.File(
                path: Path.Combine(paths.LogsRoot, "photoviewer-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                outputTemplate: "[{Timestamp:HH:mm:ss.fff} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .WriteTo.Console()
            .CreateLogger();

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

#if DEBUG
        BindingErrorTraceListener.Install();
#else
        if (SmokeTestRunner.IsRequested)
        {
            BindingErrorTraceListener.Install();
        }
#endif

        _host = Host.CreateDefaultBuilder()
            .UseSerilog()
            .ConfigureServices((_, services) =>
            {
                ConfigureServices(services, paths);
            })
            .Build();
    }

    public IServiceProvider Services => _host.Services;

    private static void ConfigureServices(IServiceCollection services, IAppDataPaths paths)
    {
        // Produktive Services-Schicht (Database, …) via AddPhotoViewerServices.
        // AppDataPaths-Instance wird hier explizit injected, sodass die im App-Constructor
        // bereits initialisierte Instance verwendet wird (Logging-Pfad).
        services.AddSingleton<IAppDataPaths>(paths);
        services.AddPhotoViewerServices();

        // WIC-Bild-Renderer (WPF-Framework) — Implementierung des in Services
        // definierten IImageThumbnailRenderer-Interfaces.
        services.AddSingleton<IImageThumbnailRenderer, WicImageThumbnailRenderer>();
        services.AddLightbox();

        // Shell: Navigation, Statusleiste, Theme, Seiten Bibliothek/Ordner/Einstellungen (Wpf/Shell)
        services.AddShell();

        // Rauchtest (nur mit PHOTOVIEWERV3_SMOKE_ROOT): Dialog- und Ordnerauswahl-Fakes vor AddGallery (TryAdd)
        SmokeTestRunner.ConfigureServices(services);

        // Galerie (Wpf/Gallery: PageSource, ViewModel, View, Dialog-/Ordnerauswahl-Dienste) und Sortieren (Wpf/Sorter)
        services.AddGallery();
        services.AddFolderActions(); // Ordner übernehmen / Ordner bewerten (Wpf/Folders, ADR-0034)
        services.AddSorterUi();
        services.AddTagManagement(); // Seite „Tags“ (Wpf/Settings/TagManagement, ADR-0021)
        services.AddCategoryManagement(); // Seite „Kategorien“ (Wpf/Settings/CategoryManagement, ADR-0027)
        services.AddSettingsInfo(); // Einstellungen › Info (Wpf/Settings/Info, ADR-0042)
        services.AddFacesUi(); // Gesichtserkennung: WIC-Lader + Einstellungs-Abschnitt (Wpf/Faces, ADR-0035)
        services.AddPersonsUi(); // Seite „Personen“: Zuordnen, Gruppen, Gesichts-Ausschnitte (Wpf/Persons, ADR-0037)
        services.AddFaceDataSettings(); // „Gesichtsdaten löschen…“ in den Einstellungen (Wpf/Settings/FaceData, 0.2.38)
        services.AddPersonRelationsSettings(); // „Personen & Beziehungen“ in den Einstellungen (Wpf/Settings/PersonRelations, ADR-0045)

        services.AddSingleton<MainWindow>();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            await _host.StartAsync();

            Log.Information("PhotoViewer V3 startet — AppData: {AppDataRoot}, Katalog: {Database}", _paths.AppDataRoot, _paths.LibraryDatabasePath);

            var locationProblem = CheckDatabaseLocation();
            if (locationProblem is not null)
            {
                Log.Error("Speicherort der Katalog-DB unbrauchbar: {Problem}", locationProblem);
                ShowFatal(locationProblem);
                Shutdown(1);
                return;
            }

            // Library-DB initialisieren (Migration anwenden, Crash-Recovery)
            try
            {
                var initializer = _host.Services.GetRequiredService<DatabaseInitializer>();
                var result = await initializer.InitializeAsync().ConfigureAwait(true);
                Log.Information(
                    "Library-DB bereit: {Path} — {Migrated} neu migriert, {Stale} Stale-Thumbnails wiederhergestellt",
                    result.DatabasePath, result.NewlyMigrated, result.StaleThumbnailsRecovered);
            }
            catch (Exception dbEx)
            {
                Log.Error(dbEx, "Library-DB-Init fehlgeschlagen");
                ShowFatal($"Datenbank-Initialisierung fehlgeschlagen:\n{dbEx.Message}\n\nApp wird beendet.");
                Shutdown(1);
                return;
            }

            // Theme (Hell/Dunkel/System) aus der Settings-Tabelle anwenden, bevor das Hauptfenster entsteht
            try
            {
                await _host.Services.GetRequiredService<IThemeService>().InitializeAsync().ConfigureAwait(true);
            }
            catch (Exception themeEx)
            {
                Log.Warning(themeEx, "Theme konnte nicht angewendet werden — Standard-Theme bleibt aktiv");
            }

            // Ordnerüberwachung (Services/Watch): Einstellungen lesen, aktive Bibliothek überwachen; Stop über Dispose beim Beenden
            try
            {
                await _host.Services.GetRequiredService<ILibraryWatchService>().StartAsync().ConfigureAwait(true);
            }
            catch (Exception watchEx)
            {
                Log.Warning(watchEx, "Ordnerüberwachung konnte nicht gestartet werden");
            }

            // Automatisches Entpacken neu gefundener Archive in Eingangsordnern (ADR-0032, Standard aus): Einstellung lesen,
            // Scan-Sperre und Nachrichten abonnieren; Stop über Dispose beim Beenden
            try
            {
                await _host.Services.GetRequiredService<IArchiveAutoExtractService>().StartAsync().ConfigureAwait(true);
            }
            catch (Exception archiveEx)
            {
                Log.Warning(archiveEx, "Automatisches Entpacken konnte nicht gestartet werden");
            }

            // Gesichtserkennung (ADR-0035, Standard aus): Einstellungen lesen, Worker starten (erster Durchgang verzögert)
            try
            {
                await _host.Services.GetRequiredService<IFaceIndexService>().StartAsync().ConfigureAwait(true);
            }
            catch (Exception faceEx)
            {
                Log.Warning(faceEx, "Gesichtserkennung konnte nicht gestartet werden");
            }

            // Gesichter-Zuordnung (ADR-0037): Vorschläge nach Index-Stapeln und Training im Hintergrund, Ausschnitt-Cache
            try
            {
                _host.Services.GetRequiredService<IFaceRecognitionService>().Start();
                _host.Services.GetRequiredService<IFaceCropService>().Start();
            }
            catch (Exception personsEx)
            {
                Log.Warning(personsEx, "Gesichter-Zuordnung konnte nicht gestartet werden");
            }

            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            mainWindow.Show();

            CleanUpOldDatabaseLocation();
        }
        catch (Exception ex)
        {
            // z. B. DI-Fehler oder XAML-Fehler beim Erzeugen des Hauptfensters — ohne diesen Fang stirbt die App stumm.
            Log.Fatal(ex, "App-Start fehlgeschlagen");
            ShowFatal($"PhotoViewer V3 konnte nicht gestartet werden:\n{ex.Message}\n\nDetails stehen im Protokoll.");
            Shutdown(1);
        }
    }

    /// <summary>
    /// Speicherort-Datei beschädigt oder gewählter Ordner ohne Katalog (Laufwerk nicht verbunden): nicht starten — sonst
    /// legte die Migration still einen leeren Katalog an.
    /// </summary>
    private string? CheckDatabaseLocation()
    {
        var locationFile = DatabaseLocationStore.GetFilePath(_paths.AppDataRoot);
        if (_locationError is not null)
        {
            return $"Der gespeicherte Speicherort der Katalog-Datenbank ist nicht lesbar:\n{_locationError}\n\n" +
                   $"Datei reparieren oder löschen (dann gilt wieder der Standardort):\n{locationFile}";
        }

        if (_paths.UsesCustomDataFolder && !File.Exists(_paths.LibraryDatabasePath))
        {
            return $"Die Katalog-Datenbank wurde nicht gefunden:\n{_paths.LibraryDatabasePath}\n\n" +
                   "Ist das Laufwerk verbunden? Danach PhotoViewer neu starten.\n" +
                   $"Zurück zum Standardort: diese Datei löschen:\n{locationFile}";
        }

        return null;
    }

    /// <summary>Nach einem Umzug: alte DB-Dateien und den alten Thumbnail-Ordner im Hintergrund löschen (ADR-0026).</summary>
    private void CleanUpOldDatabaseLocation()
    {
        var logger = _host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("PhotoViewerV3.DatabaseLocation");
        _ = Task.Run(async () =>
        {
            try
            {
                // Die alte Instanz gibt ihre Handles erst beim Beenden frei → bis zu 10 Versuche im Abstand von 2 s
                await DatabaseLocationStore.RunPendingDeletesAsync(_paths, logger, attempts: 10).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Alter Speicherort konnte nicht aufgeräumt werden");
            }
        });
    }

    /// <summary>Synchron: nach einem await liefe die Fortsetzung auf einem Dispatcher, der gerade herunterfährt.</summary>
    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("PhotoViewer V3 wird beendet (ExitCode {Code})", e.ApplicationExitCode);
        try
        {
            // IDisposable-Singletons (GalleryPageSource, ThumbnailService, LightboxService → LibVlcHost …)
            // räumt der Container in umgekehrter Erzeugungsreihenfolge auf — nur die, die wirklich erzeugt wurden.
            _host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            _host.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Fehler beim Beenden");
        }
        finally
        {
            Log.CloseAndFlush();
        }

        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Fatal(e.Exception, "Unbehandelte Ausnahme im UI-Thread");
        ShowFatal($"Unerwarteter Fehler:\n{e.Exception.Message}\n\nDetails stehen im Protokoll.");
        e.Handled = true;
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Log.Fatal(e.ExceptionObject as Exception, "Unbehandelte Ausnahme (IsTerminating={Terminating})", e.IsTerminating);
        Log.CloseAndFlush();
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unbeobachtete Task-Ausnahme");
        e.SetObserved();
    }

    /// <summary>Fehlerdialog — im unbeaufsichtigten Rauchtest nur Log (ein Dialog würde den Lauf blockieren).</summary>
    private static void ShowFatal(string message)
    {
        if (SmokeTestRunner.IsRequested)
        {
            return;
        }

        try
        {
            MessageBox.Show(message, "PhotoViewer V3", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Fehlerdialog konnte nicht angezeigt werden");
        }
    }
}
