using System.Threading;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using PhotoViewerV3.Services.Motifs;

namespace PhotoViewerV3.Shell;

/// <summary>
/// Motiv-Index in der Statusleiste (ADR-0052): „Motive: 1.234 / 19.556 · 26 ms/Foto“ mit Pause/Fortsetzen und
/// Abbrechen, solange der Index läuft, pausiert ist oder wartet; Fehler als Warnung. Muster wie
/// <c>StatusBarViewModel.FaceIndex.cs</c>: Meldungen gedrosselt vom Worker-Thread, gebündelt mit Background-Priorität.
/// </summary>
public sealed partial class StatusBarViewModel
{
    private IMotifIndexService? _motifIndex;
    private MotifIndexStatus? _queuedMotifStatus;
    private int _motifPostQueued;

    /// <summary>Text des Hinweises; null = nichts anzeigen.</summary>
    [ObservableProperty] private string? _motifIndexText;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PauseMotifIndexCommand), nameof(ResumeMotifIndexCommand))]
    [NotifyPropertyChangedFor(nameof(MotifIndexShowPause))]
    private bool _motifIndexIsPaused;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PauseMotifIndexCommand), nameof(ResumeMotifIndexCommand), nameof(CancelMotifIndexCommand))]
    [NotifyPropertyChangedFor(nameof(MotifIndexShowPause))]
    private bool _motifIndexIsActive;

    public bool MotifIndexShowPause => MotifIndexIsActive && !MotifIndexIsPaused;

    [ObservableProperty] private bool _motifIndexIsWarning;
    [ObservableProperty] private string? _motifIndexToolTip;

    private void InitMotifIndex(IMotifIndexService? motifIndex)
    {
        _motifIndex = motifIndex;
        _messenger.Register<StatusBarViewModel, MotifIndexStatusMessage>(this, static (r, m) => r.QueueMotifStatus(m.Status));
        if (motifIndex is not null)
        {
            ApplyMotifStatus(motifIndex.Status);
        }
    }

    private void QueueMotifStatus(MotifIndexStatus status)
    {
        Interlocked.Exchange(ref _queuedMotifStatus, status);
        if (Interlocked.Exchange(ref _motifPostQueued, 1) != 0)
        {
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            ApplyQueuedMotifStatus();
            return;
        }

        _ = dispatcher.InvokeAsync(ApplyQueuedMotifStatus, DispatcherPriority.Background);
    }

    private void ApplyQueuedMotifStatus()
    {
        Volatile.Write(ref _motifPostQueued, 0);
        if (Interlocked.Exchange(ref _queuedMotifStatus, null) is { } status)
        {
            ApplyMotifStatus(status);
        }
    }

    internal void ApplyMotifStatus(MotifIndexStatus status)
    {
        MotifIndexText = DescribeMotifIndex(status);
        MotifIndexIsPaused = status.State == MotifIndexState.Paused;
        MotifIndexIsActive = status.IsActive;
        MotifIndexIsWarning = status.State == MotifIndexState.Error;
        MotifIndexToolTip = status.State switch
        {
            MotifIndexState.Error => status.Message,
            _ when status.IsActive => $"Motiv-Erkennung im Hintergrund ({status.ExecutionProvider ?? "—"}): Bild-Vektoren für Fotos und Videos. "
                                      + "Pause hält nach dem aktuellen Foto an; Abbrechen beendet den Durchgang bis zum nächsten Scan oder Neustart.",
            _ => null,
        };
        UpdateActivity();
    }

    /// <summary>Text der Statusleiste je Zustand; null = ausblenden (aus, fertig, kein Modell).</summary>
    internal static string? DescribeMotifIndex(MotifIndexStatus status) => status.State switch
    {
        MotifIndexState.Running => $"Motive: {status.Done:N0} / {status.Total:N0}" + (status.MsPerPhoto > 0 ? $" · {status.MsPerPhoto:N0} ms/Foto" : string.Empty),
        MotifIndexState.Paused => $"Motive: {status.Done:N0} / {status.Total:N0} · angehalten",
        MotifIndexState.Waiting => $"Motive: {(status.Message ?? "wartet").ToLowerInvariant()}…",
        MotifIndexState.Error => $"Motiv-Erkennung: {status.Message}",
        _ => null,
    };

    private bool CanPauseMotifIndex() => MotifIndexIsActive && !MotifIndexIsPaused && _motifIndex is not null;

    private bool CanResumeMotifIndex() => MotifIndexIsPaused && _motifIndex is not null;

    private bool CanCancelMotifIndex() => MotifIndexIsActive && _motifIndex is not null;

    [RelayCommand(CanExecute = nameof(CanPauseMotifIndex))]
    private void PauseMotifIndex()
    {
        _motifIndex?.Pause();
        MotifIndexIsPaused = true;
    }

    [RelayCommand(CanExecute = nameof(CanResumeMotifIndex))]
    private void ResumeMotifIndex()
    {
        _motifIndex?.Resume();
        MotifIndexIsPaused = false;
    }

    [RelayCommand(CanExecute = nameof(CanCancelMotifIndex))]
    private void CancelMotifIndex()
    {
        _motifIndex?.CancelRun();
        _motifIndex?.Resume();
        MotifIndexIsPaused = false;
    }
}
