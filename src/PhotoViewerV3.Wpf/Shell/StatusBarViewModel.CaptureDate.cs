using System.Threading;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using PhotoViewerV3.Services.Messaging;
using PhotoViewerV3.Services.Metadata;

namespace PhotoViewerV3.Shell;

/// <summary>
/// Nachtragen des Aufnahmedatums in der Statusleiste (ADR-0046): „Aufnahmedatum: 1.234 / 19.556“, solange ein Durchgang
/// läuft; „Aufnahmedatum: wartet auf den Katalog“ während eines Umzugs. Meldungen kommen gedrosselt vom Worker-Thread und
/// werden gebündelt mit Background-Priorität übernommen (wie beim Gesichts-Index).
/// </summary>
public sealed partial class StatusBarViewModel
{
    private CaptureDateBackfillStatus? _queuedCaptureStatus;
    private int _capturePostQueued;

    /// <summary>Text des Hinweises; null = ausblenden.</summary>
    [ObservableProperty] private string? _captureDateText;

    /// <summary>Im Konstruktor aufgerufen (eine Zeile in <c>StatusBarViewModel.cs</c>).</summary>
    private void InitCaptureDate()
        => _messenger.Register<StatusBarViewModel, CaptureDateBackfillStatusMessage>(this, static (r, m) => r.QueueCaptureStatus(m.Status));

    private void QueueCaptureStatus(CaptureDateBackfillStatus status)
    {
        Interlocked.Exchange(ref _queuedCaptureStatus, status);
        if (Interlocked.Exchange(ref _capturePostQueued, 1) != 0)
        {
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            ApplyQueuedCaptureStatus();
            return;
        }

        _ = dispatcher.InvokeAsync(ApplyQueuedCaptureStatus, DispatcherPriority.Background);
    }

    private void ApplyQueuedCaptureStatus()
    {
        Volatile.Write(ref _capturePostQueued, 0);
        if (Interlocked.Exchange(ref _queuedCaptureStatus, null) is { } status)
        {
            CaptureDateText = DescribeCaptureDate(status);
        }
    }

    /// <summary>Text der Statusleiste je Zustand; null = ausblenden.</summary>
    internal static string? DescribeCaptureDate(CaptureDateBackfillStatus status)
        => status.IsRunning
            ? $"Aufnahmedatum: {status.Done:N0} / {status.Total:N0}"
            : status.Message is { } message ? $"Aufnahmedatum: {message}" : null;
}
