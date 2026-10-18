using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using Serilog;

namespace PhotoViewerV3.Shell;

/// <summary>
/// Leitet WPF-Binding-Fehler (<see cref="PresentationTraceSources.DataBindingSource"/>, ab Warning) ins
/// Serilog-Log und zählt sie — Binding-Fehler landen sonst nur im Debug-Output von Visual Studio und
/// bleiben in Rauchtests unsichtbar. Aktiv im Debug-Build und im Rauchtest (siehe App.xaml.cs).
/// </summary>
public sealed class BindingErrorTraceListener : TraceListener
{
    private static int s_installed;
    private static int s_errorCount;
    private readonly StringBuilder _buffer = new();

    /// <summary>Anzahl der seit dem Start gemeldeten Binding-Warnungen/-Fehler (thread-sicher).</summary>
    public static int ErrorCount => Volatile.Read(ref s_errorCount);

    public static void Install()
    {
        if (Interlocked.CompareExchange(ref s_installed, 1, 0) != 0)
        {
            return;
        }

        PresentationTraceSources.Refresh();
        var source = PresentationTraceSources.DataBindingSource;
        source.Listeners.Add(new BindingErrorTraceListener());
        source.Switch.Level = SourceLevels.Warning;
    }

    public override void Write(string? message) => _buffer.Append(message);

    public override void WriteLine(string? message)
    {
        _buffer.Append(message);
        Flush();
    }

    public override void Flush()
    {
        if (_buffer.Length == 0)
        {
            return;
        }

        var text = _buffer.ToString();
        _buffer.Clear();
        Record(TraceEventType.Error, text);
    }

    public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? format, params object?[]? args)
    {
        string? message = format;
        if (format is not null && args is { Length: > 0 })
        {
            try
            {
                message = string.Format(CultureInfo.InvariantCulture, format, args);
            }
            catch (FormatException)
            {
                message = format + " " + string.Join(" ", args);
            }
        }

        Record(eventType, message);
    }

    public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? message)
        => Record(eventType, message);

    private static void Record(TraceEventType type, string? message)
    {
        var text = message?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        Interlocked.Increment(ref s_errorCount);
        Log.Warning("WPF-Binding ({Type}): {Message}", type, text);
    }
}
