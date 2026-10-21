using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using PhotoViewerV3.Duplicates;
using PhotoViewerV3.Faces;
using PhotoViewerV3.Folders;
using PhotoViewerV3.Services.Faces.Indexing;
using PhotoViewerV3.Gallery;
using PhotoViewerV3.Gallery.CaptureDates;
using PhotoViewerV3.Gallery.Places;
using PhotoViewerV3.Services;
using PhotoViewerV3.Services.Database;
using PhotoViewerV3.Services.HiddenLibraries;
using PhotoViewerV3.Services.Infrastructure;
using PhotoViewerV3.Services.Library;
using PhotoViewerV3.Services.Scanning;
using PhotoViewerV3.Services.Thumbnails;
using PhotoViewerV3.Services.Archive;
using PhotoViewerV3.Services.Watch;
using PhotoViewerV3.Libraries.Linking;
using PhotoViewerV3.Lightbox;
using PhotoViewerV3.Motifs;
using PhotoViewerV3.Persons;
using PhotoViewerV3.Places;
using PhotoViewerV3.Services.Faces.Assignment;
using PhotoViewerV3.Services.Faces.Crops;
using PhotoViewerV3.Settings;
using PhotoViewerV3.Settings.CategoryManagement;
using PhotoViewerV3.Settings.FaceData;
using PhotoViewerV3.Settings.PersonRelations;
using PhotoViewerV3.Settings.TagManagement;
using PhotoViewerV3.Shell;
using PhotoViewerV3.Shell.HiddenLibraries;
using PhotoViewerV3.Sorter;
using PhotoViewerV3.Thumbnails;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;

namespace PhotoViewerV3;

/// <summary>
/// Application Bootstrap mit HostBuilder, DI-Container und Serilog gemäß ADR-0001 und ADR-0002.
/// Unbehandelte Ausnahmen (UI-Thread, AppDomain, Tasks) landen immer im Log; im Debug-Build und im
/// Rauchtest werden zusätzlich WPF-Binding-Fehler protokolliert.
/// Seit 0.2.52 (ADR-0049) hat jede Katalog-Sitzung ihren eigenen DI-Container samt Logger: normal oder eine versteckte
/// Bibliothek im Container (nur mit Startschalter <c>_hidden</c>). Der Wechsel schließt Fenster, Hintergrunddienste,
/// DB-Verbindungen und Caches der alten Sitzung vollständig und startet die neue — ohne Neustart des Prozesses.
/// </summary>
public partial class App : Application, ICatalogHostSwitcher
{
    private readonly AppDataPaths _paths;
    private readonly string? _locationError;
    private readonly Serilog.Core.Logger _normalLogger;
    private readonly CatalogLogRouter _logRouter;
    private readonly CatalogSessionManager _sessions;
    private IHost _host;
    private Serilog.Core.Logger? _hiddenLogger;

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

        // Protokolle je Katalog-Sitzung (ADR-0049): der globale Logger leitet an den Logger der aktiven Sitzung weiter
        _normalLogger = CatalogLoggers.CreateNormal(paths.LogsRoot);
        _logRouter = new CatalogLogRouter(_normalLogger);
        Log.Logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(_logRouter).CreateLogger();

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

        _sessions = CreateSessionManager(paths);
        _sessions.AttachSwitcher(this);
        _host = BuildHost(paths, isHidden: false, _normalLogger);
    }

    public IServiceProvider Services => _host.Services;

    IServiceProvider ICatalogHostSwitcher.CurrentServices => _host.Services;

    /// <summary>Startschalter <c>_hidden</c> (bzw. Rauchtest-Simulation) und die Werkzeuge der versteckten Bibliotheken.</summary>
    private static CatalogSessionManager CreateSessionManager(AppDataPaths paths)
    {
        var smoke = SmokeTestRunner.IsRequested;
        var enabled = HiddenModeSwitch.IsRequested(Environment.GetCommandLineArgs().Skip(1), smoke, Environment.GetEnvironmentVariable);
        IContainerTools containers = smoke
            ? new PhotoViewerV3.Shell.Smoke.SmokeContainerTools() // Rauchtest: nie echt ein-/aushängen
            : new ContainerTools(new ProcessRunner(), new VolumeDeviceQuery(), new VeraCryptLocator());
        var logger = new SerilogLoggerFactory(Log.Logger).CreateLogger("PhotoViewerV3.HiddenLibraries");
        return new CatalogSessionManager(
            new HiddenModeOptions(enabled, paths.AppDataRoot, paths.DataFolder, MaxFolderDepth: smoke ? int.MaxValue : 1),
            new HiddenLibraryStore(),
            new LocalDriveRootSource(),
            containers,
            TimeProvider.System,
            logger);
    }

    private IHost BuildHost(AppDataPaths paths, bool isHidden, Serilog.ILogger logger)
        => Host.CreateDefaultBuilder()
            .UseSerilog(logger, dispose: false)
            .ConfigureServices((_, services) =>
            {
                ConfigureServices(services, paths, new CatalogSessionSetup(_sessions, isHidden, _paths.AppDataRoot));
            })
            .Build();

    internal static void ConfigureServices(IServiceCollection services, IAppDataPaths paths, CatalogSessionSetup session)
    {
        // Produktive Services-Schicht (Database, …) via AddPhotoViewerServices.
        // AppDataPaths-Instance wird hier explizit injected, sodass die im App-Constructor
        // bereits initialisierte Instance verwendet wird (Logging-Pfad).
        services.AddSingleton<IAppDataPaths>(paths);
        // Katalog-Sitzung normal/versteckt (ADR-0049) — vor AddPhotoViewerServices (dort TryAdd)
        services.AddCatalogSession(paths, session);
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
        services.AddCaptureDateUi(); // „Datum setzen…“ für Galerie und Ordnerbaum (Wpf/Gallery/CaptureDates, ADR-0046)
        services.AddPlacesUi(); // „Ort setzen…“, Filter „Ort“ (Wpf/Gallery/Places, ADR-0050)
        services.AddSorterUi();
        services.AddTagManagement(); // Seite „Tags“ (Wpf/Settings/TagManagement, ADR-0021)
        services.AddCategoryManagement(); // Seite „Kategorien“ (Wpf/Settings/CategoryManagement, ADR-0027)
        services.AddSettingsInfo(); // Einstellungen › Info (Wpf/Settings/Info, ADR-0042)
        services.AddFacesUi(); // Gesichtserkennung: WIC-Lader + Einstellungs-Abschnitt (Wpf/Faces, ADR-0035)
        services.AddPersonsUi(); // Seite „Personen“: Zuordnen, Gruppen, Gesichts-Ausschnitte (Wpf/Persons, ADR-0037)
        services.AddFaceDataSettings(); // „Gesichtsdaten löschen…“ in den Einstellungen (Wpf/Settings/FaceData, 0.2.38)
        services.AddPersonRelationsSettings(); // „Personen & Beziehungen“ in den Einstellungen (Wpf/Settings/PersonRelations, ADR-0045)
        services.AddPlacesMap(); // Seite „Orte“: Weltkarte mit Foto-Gruppen, Verorten (Wpf/Places, ADR-0051)
        services.AddMotifsUi(); // Einstellungen › Motive (Wpf/Motifs, ADR-0052)
        services.AddLibraryLinkingUi(); // „Bibliotheken verknüpfen“ in Einstellungen › Bibliotheken & Eingänge (Wpf/Library/Linking, ADR-0048)
        services.AddDuplicatesUi(); // Seite „Doppelte“ (Wpf/Duplicates, ADR-0053)

        services.AddSingleton<MainWindow>();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            await StartSessionAsync(prepare: null, firstStart: true);
            CleanUpOldDatabaseLocation();
        }
        catch (SessionStartException startEx)
        {
            Log.Error(startEx.InnerException, "App-Start abgebrochen: {Problem}", startEx.Message);
            ShowFatal(startEx.Message);
            Shutdown(1);
        }
        catch (Exception ex)
        {
            // z. B. DI-Fehler oder XAML-Fehler beim Erzeugen des Hauptfensters — ohne diesen Fang stirbt die App stumm.
            Log.Fatal(ex, "App-Start fehlgeschlagen");
            ShowFatal($"Lumora konnte nicht gestartet werden:\n{ex.Message}\n\nDetails stehen im Protokoll.");
            Shutdown(1);
        }
    }

    /// <summary>Start nicht möglich (Speicherort, DB) — Meldung für den Benutzer, App wird beendet.</summary>
    private sealed class SessionStartException(string message, Exception? inner = null) : Exception(message, inner);

    /// <summary>
    /// Startet die aktive Sitzung (<see cref="_host"/>): Migration, Theme, Hintergrunddienste, ggf.
    /// <paramref name="prepare"/> (versteckte Bibliothek anlegen), Hauptfenster. Beim ersten Start zusätzlich die Prüfung
    /// des Speicherorts (ADR-0026).
    /// </summary>
    private async Task StartSessionAsync(Func<IServiceProvider, Task>? prepare, bool firstStart)
    {
        await _host.StartAsync();
        var services = _host.Services;
        var paths = services.GetRequiredService<IAppDataPaths>();
        if (firstStart)
        {
            Log.Information("Lumora startet — AppData: {AppDataRoot}, Katalog: {Database}", _paths.AppDataRoot, _paths.LibraryDatabasePath);
            if (CheckDatabaseLocation() is { } locationProblem)
            {
                throw new SessionStartException(locationProblem);
            }
        }

        // Library-DB initialisieren (Migration anwenden, Crash-Recovery)
        try
        {
            var initializer = services.GetRequiredService<DatabaseInitializer>();
            var result = await initializer.InitializeAsync().ConfigureAwait(true);
            Log.Information(
                "Library-DB bereit: {Path} — {Migrated} neu migriert, {Stale} Stale-Thumbnails wiederhergestellt",
                result.DatabasePath, result.NewlyMigrated, result.StaleThumbnailsRecovered);
        }
        catch (Exception dbEx)
        {
            throw new SessionStartException($"Datenbank-Initialisierung fehlgeschlagen:\n{dbEx.Message}\n\nApp wird beendet.", dbEx);
        }

        // Theme (Hell/Dunkel/System) aus der Settings-Tabelle anwenden, bevor das Hauptfenster entsteht
        try
        {
            await services.GetRequiredService<IThemeService>().InitializeAsync().ConfigureAwait(true);
        }
        catch (Exception themeEx)
        {
            Log.Warning(themeEx, "Theme konnte nicht angewendet werden — Standard-Theme bleibt aktiv");
        }

        // Versteckte Bibliothek: Bibliothek im neuen Katalog anlegen und ersten Scan starten (ADR-0049)
        if (prepare is not null)
        {
            await prepare(services).ConfigureAwait(true);
        }

        await StartBackgroundServicesAsync();

        var mainWindow = services.GetRequiredService<MainWindow>();
        MainWindow = mainWindow;
        mainWindow.Show();
        Log.Information("Hauptfenster gezeigt — Katalog {Database}", paths.LibraryDatabasePath);
    }

    private async Task StartBackgroundServicesAsync()
    {
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

        // Aufnahmedatum (ADR-0046): Nachtragen im Hintergrund nach jedem Scan und einmalig für bestehende Kataloge
        // Motive (ADR-0052, Standard an, läuft erst mit Modell): Einstellungen lesen, Worker starten (erster Durchgang verzögert)
        try
        {
            await _host.Services.GetRequiredService<PhotoViewerV3.Services.Motifs.IMotifIndexService>().StartAsync().ConfigureAwait(true);
        }
        catch (Exception motifEx)
        {
            Log.Warning(motifEx, "Motiv-Erkennung konnte nicht gestartet werden");
        }

        _host.Services.GetRequiredService<PhotoViewerV3.Services.Metadata.ICaptureDateService>().Start();

        // Orte (ADR-0050): Koordinaten nachtragen im Hintergrund, Ortsnamen (GeoNames) vorladen
        _host.Services.GetRequiredService<PhotoViewerV3.Services.Places.IGeoBackfillService>().Start();

        // Doppelte (ADR-0053): Prüfsummen und Bild-Fingerabdrücke im Hintergrund nach jedem Scan und einmalig für bestehende Kataloge
        _host.Services.GetRequiredService<PhotoViewerV3.Services.Duplicates.IDuplicateIndexService>().Start();
    }

    // ----- Katalogwechsel (ADR-0049) -----

    public async Task SwitchToHiddenAsync(AppDataPaths hiddenPaths, Func<IServiceProvider, Task>? prepare)
    {
        var shutdownMode = ShutdownMode;
        ShutdownMode = ShutdownMode.OnExplicitShutdown; // das alte Hauptfenster schließt, die App bleibt
        try
        {
            await TearDownSessionAsync();
            var hostBuilt = false;
            try
            {
                hiddenPaths.EnsureDirectoriesExist();
                HiddenLibraryStore.HideCatalogFolder(hiddenPaths.AppDataRoot);
                _hiddenLogger = CatalogLoggers.CreateHidden(hiddenPaths.LogsRoot);
                _logRouter.RouteToHidden(_hiddenLogger);
                Log.Information("Versteckte Sitzung geöffnet: {Database}", hiddenPaths.LibraryDatabasePath);
                _host = BuildHost(hiddenPaths, isHidden: true, _hiddenLogger);
                hostBuilt = true;
                await StartSessionAsync(prepare, firstStart: false);
            }
            catch (Exception ex)
            {
                // Details nur ins Protokoll im Container; scheitert schon das Anlegen der Ordner, nur der Typ
                _logRouter.WriteCrash(LogEventLevel.Error, ex, "Versteckte Sitzung konnte nicht gestartet werden");
                if (hostBuilt)
                {
                    await TearDownSessionAsync();
                }

                CloseHiddenLogger();
                await StartNormalOrShutdownAsync();
                throw;
            }
        }
        finally
        {
            ShutdownMode = shutdownMode;
        }
    }

    public async Task SwitchToNormalAsync()
    {
        var shutdownMode = ShutdownMode;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            Log.Information("Versteckte Sitzung wird geschlossen");
            await TearDownSessionAsync();
            CloseHiddenLogger();
            await StartNormalOrShutdownAsync();
        }
        finally
        {
            ShutdownMode = shutdownMode;
        }
    }

    public async Task<bool> IsCatalogBusyAsync()
    {
        var services = _host.Services;
        var gate = services.GetRequiredService<IScanGate>();
        var libraries = await services.GetRequiredService<ILibraryService>().GetAllAsync().ConfigureAwait(true);
        return libraries.Any(l => gate.IsBusy(l.Id)) || gate.GetWaits().Count > 0;
    }

    /// <summary>
    /// Schließt Lightbox und Dialoge, solange die Dienste der Sitzung noch leben — Code nach einem geschlossenen modalen
    /// Dialog läuft so gegen intakte Dienste weiter. false, wenn ein Fenster das Schließen ablehnt (z. B. laufendes
    /// Entpacken, das dabei abgebrochen wird).
    /// </summary>
    public async Task<bool> CloseSecondaryWindowsAsync()
    {
        foreach (var window in Windows.OfType<Window>().Where(w => !ReferenceEquals(w, MainWindow)).ToList())
        {
            try
            {
                window.Close();
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Fenster ließ sich vor dem Zusperren nicht schließen");
            }
        }

        await Dispatcher.Yield(DispatcherPriority.Background);
        await Task.Delay(200);
        await Dispatcher.Yield(DispatcherPriority.Background);
        return Windows.OfType<Window>().All(w => ReferenceEquals(w, MainWindow) || !w.IsVisible);
    }

    private async Task StartNormalSessionAsync()
    {
        _host = BuildHost(_paths, isHidden: false, _normalLogger);
        await StartSessionAsync(prepare: null, firstStart: false);
    }

    /// <summary>Normale Sitzung wieder starten; geht das nicht, beendet sich Lumora (sonst liefe sie ohne Fenster weiter).</summary>
    private async Task StartNormalOrShutdownAsync()
    {
        try
        {
            await StartNormalSessionAsync();
        }
        catch (Exception ex)
        {
            _logRouter.WriteCrash(LogEventLevel.Fatal, ex, "Normale Bibliothek konnte nicht wieder geöffnet werden");
            ShowFatal($"Lumora konnte die normale Bibliothek nicht wieder öffnen:\n{ex.Message}\n\nLumora wird beendet.");
            Shutdown(1);
            throw;
        }
    }

    /// <summary>
    /// Schließt die aktive Sitzung vollständig: alle Fenster (Hauptfenster, Lightbox, Dialoge), Hintergrunddienste und
    /// DB-Verbindungen (Dispose des Containers), Empfänger des Messengers, Bilder im Speicher (GC). Ein Fenster, das das
    /// Schließen ablehnt, wird wenigstens verborgen.
    /// </summary>
    private async Task TearDownSessionAsync()
    {
        foreach (var window in Windows.OfType<Window>().ToList())
        {
            try
            {
                window.Close();
                if (window.IsVisible)
                {
                    window.Hide();
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Fenster ließ sich beim Katalogwechsel nicht schließen");
            }
        }

        var host = _host;
        try
        {
            await host.StopAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Sitzung ließ sich nicht sauber anhalten");
        }

        host.Dispose();

        // Empfänger der alten Sitzung dürfen keine Nachricht der neuen mehr bekommen (WeakReferenceMessenger.Default ist global)
        WeakReferenceMessenger.Default.Reset();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private void CloseHiddenLogger()
    {
        _logRouter.RouteToNormal(_normalLogger);
        _hiddenLogger?.Dispose();
        _hiddenLogger = null;
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
                   "Ist das Laufwerk verbunden? Danach Lumora neu starten.\n" +
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

    /// <summary>
    /// Synchron: nach einem await liefe die Fortsetzung auf einem Dispatcher, der gerade herunterfährt. War eine versteckte
    /// Bibliothek offen, wird ihre Sitzung samt Protokoll geschlossen und danach ggf. der Container ausgehängt (ADR-0049).
    /// </summary>
    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("Lumora wird beendet (ExitCode {Code})", e.ApplicationExitCode);
        var wasHidden = _sessions.IsHiddenSessionActive;
        _sessions.PrepareExit();
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
            if (wasHidden)
            {
                CloseHiddenLogger();
                if (_sessions.DismountAfterExit(TimeSpan.FromSeconds(30)) is { Success: false } dismount)
                {
                    ShowFatal($"Der Container wurde nicht ausgehängt:\n{dismount.Message}");
                }
            }

            Log.CloseAndFlush();
            _normalLogger.Dispose();
        }

        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logRouter.WriteCrash(LogEventLevel.Fatal, e.Exception, "Unbehandelte Ausnahme im UI-Thread");
        ShowFatal($"Unerwarteter Fehler:\n{e.Exception.Message}\n\nDetails stehen im Protokoll.");
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        _logRouter.WriteCrash(LogEventLevel.Fatal, e.ExceptionObject as Exception, $"Unbehandelte Ausnahme (IsTerminating={e.IsTerminating})");
        Log.CloseAndFlush();
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _logRouter.WriteCrash(LogEventLevel.Error, e.Exception, "Unbeobachtete Task-Ausnahme");
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
            MessageBox.Show(message, "Lumora", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Fehlerdialog konnte nicht angezeigt werden");
        }
    }
}
