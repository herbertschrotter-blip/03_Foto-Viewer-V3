using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using Microsoft.Extensions.Logging;

namespace Lumora.Shell;

/// <summary>
/// Hauptfenster während eines langen Vorgangs sperren und die App neu starten (nach dem Umzug der Katalog-DB, ADR-0026).
/// </summary>
public interface IAppRestartService
{
    /// <summary>Hauptfenster gegen Eingaben sperren (Warte-Cursor) bzw. wieder freigeben.</summary>
    void SetInputBlocked(bool blocked);

    /// <summary>Startet eine neue Instanz mit denselben Argumenten und beendet diese; false, wenn der Start scheitert.</summary>
    bool Restart();
}

public sealed class AppRestartService : IAppRestartService
{
    private readonly ILogger<AppRestartService> _logger;

    public AppRestartService(ILogger<AppRestartService> logger)
    {
        _logger = logger;
    }

    public void SetInputBlocked(bool blocked)
    {
        if (Application.Current?.MainWindow is { } window)
        {
            window.IsEnabled = !blocked;
        }

        Mouse.OverrideCursor = blocked ? Cursors.Wait : null;
    }

    public bool Restart()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            _logger.LogError("Neustart nicht möglich: Pfad der laufenden Anwendung unbekannt");
            return false;
        }

        try
        {
            var start = new ProcessStartInfo(exe) { UseShellExecute = false };
            foreach (var arg in Environment.GetCommandLineArgs().Skip(1))
            {
                start.ArgumentList.Add(arg);
            }

            Process.Start(start);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Neustart nicht möglich: {Exe}", exe);
            return false;
        }

        _logger.LogInformation("Neustart: neue Instanz gestartet, diese wird beendet");
        Mouse.OverrideCursor = null;
        Application.Current?.Shutdown();
        return true;
    }
}
