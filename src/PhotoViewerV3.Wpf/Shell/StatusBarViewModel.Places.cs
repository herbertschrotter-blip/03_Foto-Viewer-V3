using System.Threading;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using PhotoViewerV3.Services.Messaging;
using PhotoViewerV3.Services.Places;

namespace PhotoViewerV3.Shell;

/// <summary>
/// Nachtragen der Koordinaten in der Statusleiste (ADR-0050): „Orte: 1.234 / 19.556“, solange ein Durchgang läuft;
/// „Orte: wartet auf den Katalog“ während eines Umzugs. Gedrosselte Meldungen vom Worker-Thread, gebündelt mit
/// Background-Priorität übernommen (wie das Aufnahmedatum).
/// </summary>
public sealed partial class StatusBarViewModel
{
    private GeoBackfillStatus? _queuedGeoStatus;
    private int _geoPostQueued;

    /// <summary>Text des Hinweises; null = ausblenden.</summary>
    [ObservableProperty] private string? _geoBackfillText;

    /// <summary>Im Konstruktor aufgerufen (eine Zeile in <c>StatusBarViewModel.cs</c>).</summary>
    private void InitPlaces()
        => _messenger.Register<StatusBarViewModel, GeoBackfillStatusMessage>(this, static (r, m) => r.QueueGeoStatus(m.Status));

    private void QueueGeoStatus(GeoBackfillStatus status)
    {
        Interlocked.Exchange(ref _queuedGeoStatus, status);
        if (Interlocked.Exchange(ref _geoPostQueued, 1) != 0)
        {
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            ApplyQueuedGeoStatus();
            return;
        }

        _ = dispatcher.InvokeAsync(ApplyQueuedGeoStatus, DispatcherPriority.Background);
    }

    private void ApplyQueuedGeoStatus()
    {
        Volatile.Write(ref _geoPostQueued, 0);
        if (Interlocked.Exchange(ref _queuedGeoStatus, null) is { } status)
        {
            GeoBackfillText = DescribeGeoBackfill(status);
        }
    }

    /// <summary>Text der Statusleiste je Zustand; null = ausblenden.</summary>
    internal static string? DescribeGeoBackfill(GeoBackfillStatus status)
        => status.IsRunning
            ? $"Orte: {status.Done:N0} / {status.Total:N0}"
            : status.Message is { } message ? $"Orte: {message}" : null;
}
