using System.IO;
using System.Windows;
using PhotoViewerV3.Services.Infrastructure;
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
        var paths = new AppDataPaths();
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

    private static void ConfigureServices(IServiceCollection services, IAppDataPaths paths)
    {
        services.AddSingleton<IAppDataPaths>(paths);
        services.AddSingleton<MainWindow>();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        await _host.StartAsync();

        Log.Information("PhotoViewer V3 startet — AppData: {AppDataRoot}", _host.Services.GetRequiredService<IAppDataPaths>().AppDataRoot);

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        mainWindow.Show();

        base.OnStartup(e);
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        Log.Information("PhotoViewer V3 wird beendet");
        await _host.StopAsync();
        _host.Dispose();
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
