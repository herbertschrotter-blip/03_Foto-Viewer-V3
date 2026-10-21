using System;
using System.IO;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace PhotoViewerV3.Shell.HiddenLibraries;

/// <summary>
/// Protokolle je Katalog-Sitzung (ADR-0049). Jede Sitzung bekommt ihren eigenen Serilog-Logger: der normale schreibt nach
/// <c>%LocalAppData%\PhotoViewerV3\logs\photoviewer-*.log</c> (+ Konsole), der einer versteckten Bibliothek NUR nach
/// <c>&lt;Bibliothek&gt;\.lumora\logs\lumora-*.log</c>. Nachzügler einer beendeten versteckten Sitzung (Hintergrund-Tasks)
/// schreiben in deren geschlossenen Logger und landen so nirgends — nie im normalen Protokoll.
/// </summary>
public static class CatalogLoggers
{
    public const string NormalFilePattern = "photoviewer-.log";
    public const string HiddenFilePattern = "lumora-.log";

    private const string Template = "[{Timestamp:HH:mm:ss.fff} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

    public static Logger CreateNormal(string logsRoot)
        => Base()
            .WriteTo.File(Path.Combine(logsRoot, NormalFilePattern), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14, outputTemplate: Template)
            .WriteTo.Console()
            .CreateLogger();

    /// <summary>Nur Datei im Container, keine Konsole (ein umgeleiteter Konsolenstrom läge außerhalb).</summary>
    public static Logger CreateHidden(string logsRoot)
    {
        Directory.CreateDirectory(logsRoot);
        return Base()
            .WriteTo.File(Path.Combine(logsRoot, HiddenFilePattern), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14, outputTemplate: Template)
            .CreateLogger();
    }

    private static LoggerConfiguration Base()
        => new LoggerConfiguration()
            .MinimumLevel.Information()
            // EF Core protokolliert sonst jeden SQL-Befehl (tausende Zeilen pro Sitzung, I/O auf den Thumbnail-Workern)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning);
}

/// <summary>
/// Ziel des globalen <see cref="Log.Logger"/> (statische Aufrufe: Start, unbehandelte Ausnahmen, Binding-Fehler): leitet
/// an den Logger der aktiven Sitzung weiter. Nach einer versteckten Sitzung schreibt <see cref="WriteCrash"/> nur noch den
/// Ausnahmetyp ins normale Protokoll — Meldungen und Stapel könnten Pfade aus dem Container enthalten.
/// </summary>
public sealed class CatalogLogRouter : ILogEventSink
{
    private volatile ILogger _current;

    public CatalogLogRouter(ILogger initial)
    {
        _current = initial;
    }

    /// <summary>true, sobald in diesem Prozess eine versteckte Sitzung geöffnet war.</summary>
    public bool HiddenSessionWasOpened { get; private set; }

    /// <summary>true, solange der Logger einer versteckten Sitzung das Ziel ist.</summary>
    public bool IsHiddenTarget { get; private set; }

    public void RouteToHidden(ILogger hidden)
    {
        HiddenSessionWasOpened = true;
        IsHiddenTarget = true;
        _current = hidden;
    }

    public void RouteToNormal(ILogger normal)
    {
        IsHiddenTarget = false;
        _current = normal;
    }

    public void Emit(LogEvent logEvent) => _current.Write(logEvent);

    /// <summary>
    /// Absturzbericht über den globalen Logger: während der versteckten Sitzung vollständig (er landet im Container),
    /// danach im normalen Protokoll nur Ebene, Text ohne Platzhalter und Ausnahmetyp.
    /// </summary>
    public void WriteCrash(LogEventLevel level, Exception? exception, string message)
    {
        if (HiddenSessionWasOpened && !IsHiddenTarget)
        {
            _current.Write(level, "{Message} ({Type}; Details unterdrückt)", message, exception?.GetType().FullName ?? "-");
            return;
        }

        _current.Write(level, exception, "{Message}", message);
    }
}
