using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using PhotoViewerV3.Gallery;
using PhotoViewerV3.Services;
using PhotoViewerV3.Services.Database;
using PhotoViewerV3.Services.Infrastructure;
using PhotoViewerV3.Services.Thumbnails;
using PhotoViewerV3.Lightbox;
using PhotoViewerV3.Settings;
using PhotoViewerV3.Shell;
using PhotoViewerV3.Sorter;
using PhotoViewerV3.Thumbnails;
using PhotoViewerV3.Spike02.Data;
using PhotoViewerV3.Spike02.ViewModels;
using PhotoViewerV3.Spike02.Views;
using PhotoViewerV3.Spike03.Services;
using PhotoViewerV3.Spike03.ViewModels;
using PhotoViewerV3.Spike03.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace PhotoViewerV3;

/// <summary>
/// Application Bootstrap mit HostBuilder, DI-Container und Serilog gemäß ADR-0001 und ADR-0002.
/// Unbehandelte Ausnahmen (UI-Thread, AppDomain, Tasks) landen immer im Log; im Debug-Build und im
/// Rauchtest werden zusätzlich WPF-Binding-Fehler protokolliert.
/// </summary>
public partial class App : Application
{
    private readonly IHost _host;

    public App()
    {
        // Produktiv %LocalAppData%\PhotoViewerV3; per PHOTOVIEWERV3_APPDATA umlenkbar (Rauchtests mit frischer DB)
        var paths = AppDataRootOverride.Resolve();
        paths.EnsureDirectoriesExist();

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
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

        // Spike #2 — Virtual Grid
        services.AddSingleton<GalleryPageSource>();
        services.AddSingleton<GalleryViewModel>();
        services.AddTransient<GalleryView>();
        services.AddGallery();
        services.AddSorterUi();

        // Spike #3 — LibVLCSharp Lightbox
        services.AddSingleton<SpikeMediaDownloader>();
        services.AddSingleton<SpikeLightboxViewModel>();
        services.AddTransient<SpikeLightboxView>();

        services.AddSingleton<MainWindow>();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            await _host.StartAsync();

            Log.Information("PhotoViewer V3 startet — AppData: {AppDataRoot}", _host.Services.GetRequiredService<IAppDataPaths>().AppDataRoot);

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

            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            mainWindow.Show();
        }
        catch (Exception ex)
        {
            // z. B. DI-Fehler oder XAML-Fehler beim Erzeugen des Hauptfensters — ohne diesen Fang stirbt die App stumm.
            Log.Fatal(ex, "App-Start fehlgeschlagen");
            ShowFatal($"PhotoViewer V3 konnte nicht gestartet werden:\n{ex.Message}\n\nDetails stehen im Protokoll.");
            Shutdown(1);
        }
    }

    /// <summary>Synchron: nach einem await liefe die Fortsetzung auf einem Dispatcher, der gerade herunterfährt.</summary>
    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("PhotoViewer V3 wird beendet (ExitCode {Code})", e.ApplicationExitCode);
        try
        {
            // IDisposable-Singletons (GalleryPageSource, ThumbnailService, LightboxService → LibVlcHost, Spike03 …)
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
