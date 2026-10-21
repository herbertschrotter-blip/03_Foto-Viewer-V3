using System.Threading;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Lumora.Services.Faces.Indexing;

namespace Lumora.Shell;

/// <summary>
/// Gesichts-Index in der Statusleiste (0.2.34, ADR-0035): „Gesichter: 1.234 / 19.556 · 37 ms/Foto“ mit Pause/Fortsetzen
/// und Abbrechen, solange der Index läuft, pausiert ist oder auf den Katalog wartet; Fehler als Warnung. Die
/// Zustandsmeldungen kommen gedrosselt vom Worker-Thread und werden gebündelt mit Background-Priorität übernommen.
/// </summary>
public sealed partial class StatusBarViewModel
{
    private IFaceIndexService? _faceIndex;
    private FaceIndexStatus? _queuedFaceStatus;
    private int _facePostQueued;

    /// <summary>Text des Hinweises; null = nichts anzeigen.</summary>
    [ObservableProperty] private string? _faceIndexText;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PauseFaceIndexCommand), nameof(ResumeFaceIndexCommand))]
    [NotifyPropertyChangedFor(nameof(FaceIndexShowPause))]
    private bool _faceIndexIsPaused;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PauseFaceIndexCommand), nameof(ResumeFaceIndexCommand), nameof(CancelFaceIndexCommand))]
    [NotifyPropertyChangedFor(nameof(FaceIndexShowPause))]
    private bool _faceIndexIsActive;

    /// <summary>„Pause“ nur, solange der Index läuft oder wartet.</summary>
    public bool FaceIndexShowPause => FaceIndexIsActive && !FaceIndexIsPaused;

    [ObservableProperty] private bool _faceIndexIsWarning;
    [ObservableProperty] private string? _faceIndexToolTip;

    private void InitFaceIndex(IFaceIndexService? faceIndex)
    {
        _faceIndex = faceIndex;
        _messenger.Register<StatusBarViewModel, FaceIndexStatusMessage>(this, static (r, m) => r.QueueFaceStatus(m.Status));
        if (faceIndex is not null)
        {
            ApplyFaceStatus(faceIndex.Status);
        }
    }

    private void QueueFaceStatus(FaceIndexStatus status)
    {
        Interlocked.Exchange(ref _queuedFaceStatus, status);
        if (Interlocked.Exchange(ref _facePostQueued, 1) != 0)
        {
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            ApplyQueuedFaceStatus();
            return;
        }

        _ = dispatcher.InvokeAsync(ApplyQueuedFaceStatus, DispatcherPriority.Background);
    }

    private void ApplyQueuedFaceStatus()
    {
        Volatile.Write(ref _facePostQueued, 0);
        if (Interlocked.Exchange(ref _queuedFaceStatus, null) is { } status)
        {
            ApplyFaceStatus(status);
        }
    }

    internal void ApplyFaceStatus(FaceIndexStatus status)
    {
        FaceIndexText = DescribeFaceIndex(status);
        FaceIndexIsPaused = status.State == FaceIndexState.Paused;
        FaceIndexIsActive = status.IsActive;
        FaceIndexIsWarning = status.State == FaceIndexState.Error;
        FaceIndexToolTip = status.State switch
        {
            FaceIndexState.Error => status.Message,
            _ when status.IsActive => $"Gesichtserkennung im Hintergrund ({status.ExecutionProvider ?? "—"}): {status.FacesFound:N0} Gesichter in diesem Durchgang. "
                                      + "Pause hält nach dem aktuellen Foto an; Abbrechen beendet den Durchgang bis zum nächsten Scan oder Neustart.",
            _ => null,
        };
        UpdateActivity();
    }

    /// <summary>Text der Statusleiste je Zustand; null = ausblenden (aus, fertig, kein Modell).</summary>
    internal static string? DescribeFaceIndex(FaceIndexStatus status) => status.State switch
    {
        FaceIndexState.Running => $"Gesichter: {status.Done:N0} / {status.Total:N0}" + (status.MsPerPhoto > 0 ? $" · {status.MsPerPhoto:N0} ms/Foto" : string.Empty),
        FaceIndexState.Paused => $"Gesichter: {status.Done:N0} / {status.Total:N0} · angehalten",
        FaceIndexState.Waiting => "Gesichter: wartet auf den Katalog…",
        FaceIndexState.Error => $"Gesichtserkennung: {status.Message}",
        _ => null,
    };

    private bool CanPauseFaceIndex() => FaceIndexIsActive && !FaceIndexIsPaused && _faceIndex is not null;

    private bool CanResumeFaceIndex() => FaceIndexIsPaused && _faceIndex is not null;

    private bool CanCancelFaceIndex() => FaceIndexIsActive && _faceIndex is not null;

    [RelayCommand(CanExecute = nameof(CanPauseFaceIndex))]
    private void PauseFaceIndex()
    {
        _faceIndex?.Pause();
        FaceIndexIsPaused = true;
    }

    [RelayCommand(CanExecute = nameof(CanResumeFaceIndex))]
    private void ResumeFaceIndex()
    {
        _faceIndex?.Resume();
        FaceIndexIsPaused = false;
    }

    [RelayCommand(CanExecute = nameof(CanCancelFaceIndex))]
    private void CancelFaceIndex()
    {
        // Abbrechen hebt auch eine Pause auf — sonst hinge der nächste Durchgang unsichtbar angehalten.
        _faceIndex?.CancelRun();
        _faceIndex?.Resume();
        FaceIndexIsPaused = false;
    }
}
