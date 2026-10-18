using System;
using System.IO;
using System.Windows;
using PhotoViewerV3.Gallery;
using PhotoViewerV3.Services;
using PhotoViewerV3.Services.Database;
using PhotoViewerV3.Services.Infrastructure;
using PhotoViewerV3.Services.Thumbnails;
using PhotoViewerV3.Lightbox;
using PhotoViewerV3.Settings;
using PhotoViewerV3.Shell;
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

        // Spike #2 — Virtual Grid
        services.AddSingleton<GalleryPageSource>();
        services.AddSingleton<GalleryViewModel>();
        services.AddTransient<GalleryView>();
        services.AddGallery();

        // Spike #3 — LibVLCSharp Lightbox
        services.AddSingleton<SpikeMediaDownloader>();
        services.AddSingleton<SpikeLightboxViewModel>();
        services.AddTransient<SpikeLightboxView>();

        services.AddSingleton<MainWindow>();
    }

    protected override async void OnStartup(StartupEventArgs e)
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
            MessageBox.Show(
                $"Datenbank-Initialisierung fehlgeschlagen:\n{dbEx.Message}\n\nApp wird beendet.",
                "PhotoViewer V3",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
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

        base.OnStartup(e);
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        Log.Information("PhotoViewer V3 wird beendet");
        _host.Services.GetService<GalleryPageSource>()?.Dispose();
        _host.Services.GetService<SpikeLightboxViewModel>()?.Dispose();
        _host.Services.GetService<SpikeMediaDownloader>()?.Dispose();
        await _host.StopAsync();
        _host.Dispose();
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
