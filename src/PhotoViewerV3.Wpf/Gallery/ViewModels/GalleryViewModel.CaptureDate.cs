using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using PhotoViewerV3.Gallery.CaptureDates;
using PhotoViewerV3.Gallery.Data;
using PhotoViewerV3.Services.Messaging;

namespace PhotoViewerV3.Gallery.ViewModels;

/// <summary>
/// Aufnahmedatum in der Galerie (ADR-0046): „Datum setzen…“ (Aktionsleiste, Rechtsklick) über <see cref="CaptureDateActions"/>,
/// Rückgängig gemeinsam mit den Datei-Operationen (der jüngste rückgängig machbare Eintrag gewinnt) und Neuladen nach
/// <see cref="CaptureDatesChangedMessage"/> — bei Sortierung nach Aufnahmedatum oder wenn Dateien der Seite betroffen sind.
/// </summary>
public sealed partial class GalleryViewModel
{
    /// <summary>So viele jüngste Batches prüft „Rückgängig verfügbar?“ (Datei-Operationen und „Datum setzen“ teilen die Tabelle).</summary>
    private const int UndoBatchWindow = 50;

    private CaptureDateActions? _captureDateActions;

    /// <summary>true bei Sortierung nach Aufnahmedatum (Standard).</summary>
    public bool IsSortedByCaptureDate => SelectedSort.Category is null && SelectedSort.Sort is GallerySort.CapturedDesc or GallerySort.CapturedAsc;

    private void InitCaptureDate(CaptureDateActions? actions)
    {
        _captureDateActions = actions;
        _messenger.Register<GalleryViewModel, CaptureDatesChangedMessage>(this, static (r, m) => r.RunOnUi(() => r.HandleCaptureDatesChangedAsync(m)));
        PropertyChanged += OnCaptureDateCommandStateChanged;
    }

    private void OnCaptureDateCommandStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SelectedCount) or nameof(IsOperationRunning))
        {
            SetSelectionCaptureDateCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task HandleCaptureDatesChangedAsync(CaptureDatesChangedMessage m)
    {
        if (!HasLibrary || !InScope(m.LibraryId))
        {
            return;
        }

        if (IsSortedByCaptureDate || m.FileIds.Any(_currentItems.ContainsKey))
        {
            ScheduleReload();
        }

        // „Datum setzen“ aus dem Ordnerbaum bzw. Rückgängig: Knopf „Rückgängig“ nachziehen.
        await RefreshCanUndoAsync();
    }

    private bool CanSetSelectionCaptureDate() => CanActOnSelection && _captureDateActions is not null;

    /// <summary>„Datum setzen…“ für die Auswahl.</summary>
    [RelayCommand(CanExecute = nameof(CanSetSelectionCaptureDate))]
    private Task SetSelectionCaptureDateAsync()
        => RunOperationAsync("Datum setzen", async () =>
        {
            var ids = SelectedIdsSnapshot();
            if (_captureDateActions is null || ids.Count == 0)
            {
                return;
            }

            var text = await _captureDateActions.SetForFilesAsync(ids, OwnerWindowResolver?.Invoke() ?? ActiveWindow());
            if (text is not null)
            {
                SetResultStatus(text);
            }
        });

    /// <summary>
    /// Rückgängig: nimmt „Datum setzen“ zurück, wenn das der jüngste rückgängig machbare Eintrag ist (sonst false — dann
    /// die Datei-Operation). Gemeinsame Reihenfolge über die Batch-Id (dieselbe Tabelle OperationBatches).
    /// </summary>
    private async Task<bool> TryUndoCaptureDateAsync()
    {
        if (_captureDateActions?.Service is not { } service || await service.GetUndoableBatchIdAsync() is not { } batchId)
        {
            return false;
        }

        var batches = await _fileOps.GetRecentBatchesAsync(UndoBatchWindow);
        if (batches.Where(b => b.CanUndo).Select(b => (int?)b.Id).Max() is { } newestFileOperation && newestFileOperation > batchId)
        {
            return false;
        }

        var result = await service.UndoAsync(batchId);
        if (result is null)
        {
            return false;
        }

        _logger.LogInformation("Rückgängig: Datum setzen (Batch {BatchId}) — {Count} Datei(en)", batchId, result.Changed);
        SetResultStatus($"Rückgängig: {result.Description} — {result.Changed:N0} Datei(en) zurückgesetzt.");
        return true;
    }

    private async Task<bool> HasCaptureDateUndoAsync()
        => _captureDateActions?.Service is { } service && await service.GetUndoableBatchIdAsync() is not null;
}
