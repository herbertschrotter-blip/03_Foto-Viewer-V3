using System.Threading;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Lumora.Services.Quality;

namespace Lumora.Shell;

/// <summary>
/// Foto-Güte in der Statusleiste (ADR-0057): Fortschritt des Hintergrund-Index („Foto-Güte: 1.234 von 5.678 bewertet“,
/// danach ggf. „Foto-Güte: Bildwirkung 300 von 900“). Gedrosselte Meldungen vom Worker-Thread, gebündelt mit
/// Background-Priorität übernommen.
/// </summary>
public sealed partial class StatusBarViewModel
{
    private FileQualityIndexStatus? _queuedQualityStatus;
    private int _qualityPostQueued;

    /// <summary>Fortschritt; null = ausblenden.</summary>
    [ObservableProperty] private string? _qualityIndexText;

    /// <summary>Im Konstruktor aufgerufen (eine Zeile in <c>StatusBarViewModel.cs</c>).</summary>
    private void InitQuality()
        => _messenger.Register<StatusBarViewModel, FileQualityIndexStatusMessage>(this, static (r, m) => r.QueueQualityStatus(m.Status));

    /// <summary>Text des Fortschritts; null = ausblenden.</summary>
    internal static string? DescribeQualityIndex(FileQualityIndexStatus status)
        => status.IsRunning
            ? status.Phase == FileQualityIndexStatus.AestheticPhase
                ? $"Foto-Güte: Bildwirkung {status.Done:N0} von {status.Total:N0}"
                : $"Foto-Güte: {status.Done:N0} von {status.Total:N0} bewertet"
            : status.Message is { } message ? $"Foto-Güte: {message}" : null;

    private void QueueQualityStatus(FileQualityIndexStatus status)
    {
        Interlocked.Exchange(ref _queuedQualityStatus, status);
        if (Interlocked.Exchange(ref _qualityPostQueued, 1) != 0)
        {
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            ApplyQueuedQualityStatus();
            return;
        }

        _ = dispatcher.InvokeAsync(ApplyQueuedQualityStatus, DispatcherPriority.Background);
    }

    private void ApplyQueuedQualityStatus()
    {
        Volatile.Write(ref _qualityPostQueued, 0);
        if (Interlocked.Exchange(ref _queuedQualityStatus, null) is { } status)
        {
            QualityIndexText = DescribeQualityIndex(status);
        }
    }
}
