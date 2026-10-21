using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.Logging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using PhotoViewerV3.Duplicates;
using PhotoViewerV3.Services.Duplicates;

namespace PhotoViewerV3.Shell;

/// <summary>
/// Doppelte in der Statusleiste (ADR-0053): Fortschritt des Hintergrund-Index („Doppelte: Prüfsummen 1.234 / 5.678“) und
/// der Hinweis „N Dateien im Eingang sind schon in der Bibliothek — Anzeigen“ (öffnet die Seite „Doppelte“ gefiltert;
/// ✕ blendet ihn aus). Gedrosselte Meldungen vom Worker-Thread, gebündelt mit Background-Priorität übernommen.
/// </summary>
public sealed partial class StatusBarViewModel
{
    private IDuplicatesPageNavigator? _duplicatesNavigator;
    private DuplicateIndexStatus? _queuedDuplicateStatus;
    private int _duplicatePostQueued;

    /// <summary>Fortschritt; null = ausblenden.</summary>
    [ObservableProperty] private string? _duplicateIndexText;

    /// <summary>Hinweis „… im Eingang sind schon in der Bibliothek“; null = ausblenden.</summary>
    [ObservableProperty] private string? _inboxDuplicatesText;

    /// <summary>Im Konstruktor aufgerufen (eine Zeile in <c>StatusBarViewModel.cs</c>).</summary>
    private void InitDuplicates(IDuplicatesPageNavigator? navigator)
    {
        _duplicatesNavigator = navigator;
        _messenger.Register<StatusBarViewModel, DuplicateIndexStatusMessage>(this, static (r, m) => r.QueueDuplicateStatus(m.Status));
        _messenger.Register<StatusBarViewModel, InboxDuplicatesFoundMessage>(this, static (r, m) => UiThread.Post(() => _ = r.ShowInboxHintAsync(m)));
    }

    /// <summary>Hinweis nur für eine Bibliothek des angezeigten Bereichs — „Anzeigen“ öffnet ja diesen Bereich.</summary>
    private async Task ShowInboxHintAsync(InboxDuplicatesFoundMessage message)
    {
        try
        {
            if (_libraryScope is not null && !(await _libraryScope.GetCurrentAsync().ConfigureAwait(true)).Contains(message.LibraryId))
            {
                return;
            }

            InboxDuplicatesText = DescribeInboxDuplicates(message.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Statusleiste: Bereich für den Doppelten-Hinweis nicht lesbar");
        }
    }

    /// <summary>Text des Eingang-Hinweises.</summary>
    internal static string DescribeInboxDuplicates(int count)
        => count == 1
            ? "1 Datei im Eingang ist schon in der Bibliothek"
            : $"{count:N0} Dateien im Eingang sind schon in der Bibliothek";

    /// <summary>Text des Fortschritts; null = ausblenden.</summary>
    internal static string? DescribeDuplicateIndex(DuplicateIndexStatus status)
        => status.IsRunning
            ? $"Doppelte: {status.Phase} {status.Done:N0} / {status.Total:N0}"
            : status.Message is { } message ? $"Doppelte: {message}" : null;

    [RelayCommand]
    private void ShowInboxDuplicates()
    {
        InboxDuplicatesText = null;
        _duplicatesNavigator?.ShowInboxDuplicates();
    }

    [RelayCommand]
    private void DismissInboxDuplicates() => InboxDuplicatesText = null;

    private void QueueDuplicateStatus(DuplicateIndexStatus status)
    {
        Interlocked.Exchange(ref _queuedDuplicateStatus, status);
        if (Interlocked.Exchange(ref _duplicatePostQueued, 1) != 0)
        {
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            ApplyQueuedDuplicateStatus();
            return;
        }

        _ = dispatcher.InvokeAsync(ApplyQueuedDuplicateStatus, DispatcherPriority.Background);
    }

    private void ApplyQueuedDuplicateStatus()
    {
        Volatile.Write(ref _duplicatePostQueued, 0);
        if (Interlocked.Exchange(ref _queuedDuplicateStatus, null) is { } status)
        {
            DuplicateIndexText = DescribeDuplicateIndex(status);
        }
    }
}
