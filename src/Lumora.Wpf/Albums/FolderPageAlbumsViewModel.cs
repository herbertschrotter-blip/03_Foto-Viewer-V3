using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Lumora.Gallery.ViewModels;
using Lumora.Services.Operations;

namespace Lumora.Albums;

/// <summary>
/// Ordner-Seite (ADR-0054): Ablegen von Galerie-Dateien auf einen Ordner im Ordnerbaum (ohne Taste/Alt/Strg verknüpfen,
/// mit Umschalt verschieben — nach Rückfrage, protokolliert, rückgängig machbar) und auf ein Album der Liste „Alben“ unter
/// dem Baum (hinzufügen). Klick auf ein Album öffnet es in der Galerie rechts.
/// </summary>
public sealed partial class FolderPageAlbumsViewModel : ObservableObject
{
    private readonly AlbumActions _actions;
    private readonly IFileOperationService _fileOps;
    private readonly ILogger<FolderPageAlbumsViewModel> _logger;

    public FolderPageAlbumsViewModel(AlbumActions actions, GalleryViewModel gallery, IFileOperationService fileOps, ILogger<FolderPageAlbumsViewModel> logger)
    {
        _actions = actions;
        Gallery = gallery;
        _fileOps = fileOps;
        _logger = logger;
    }

    /// <summary>Gemeinsames Galerie-ViewModel (Album-Liste: <see cref="GalleryViewModel.AlbumFilterEntries"/>).</summary>
    public GalleryViewModel Gallery { get; }

    /// <summary>Ergebnis des letzten Ablegens (unter dem Baum).</summary>
    [ObservableProperty] private string? _statusText;

    [RelayCommand]
    private Task OpenAlbumAsync(AlbumFilterEntry? entry) => entry is null ? Task.CompletedTask : Gallery.OpenAlbumAsync(entry.AlbumId);

    /// <summary>Dateien auf einen Ordner abgelegt.</summary>
    public async Task DropOnFolderAsync(int folderId, string folderName, string folderPath, IReadOnlyList<int> fileIds, bool move)
    {
        if (fileIds.Count == 0)
        {
            return;
        }

        try
        {
            if (!move)
            {
                var result = await Gallery.LinkToFolderAsync(folderId, folderName, fileIds);
                StatusText = result is null
                    ? null
                    : result.Linked > 0
                        ? $"{result.Linked:N0} mit „{folderName}“ verknüpft — die Fotos bleiben, wo sie liegen."
                        : result.OtherLibrary > 0
                            ? "Nur Fotos derselben Bibliothek lassen sich in diesen Ordner verknüpfen."
                            : $"Schon in „{folderName}“ (echt oder verknüpft).";
                return;
            }

            var text = fileIds.Count == 1 ? "1 Datei" : $"{fileIds.Count:N0} Dateien";
            if (!await _actions.Dialogs.ConfirmAsync(
                    "Verschieben?",
                    $"{text} wirklich nach „{folderPath}“ VERSCHIEBEN? (Ohne Umschalttaste abgelegt wird nur verknüpft.) Verknüpfungen und Alben bleiben erhalten; Rückgängig ist möglich.",
                    "Verschieben"))
            {
                return;
            }

            var moved = await _fileOps.MoveAsync(fileIds, folderPath);
            StatusText = moved.Failed == 0
                ? $"{moved.Succeeded:N0} nach „{folderName}“ verschoben (Rückgängig in der Galerie)."
                : $"{moved.Succeeded:N0} verschoben, {moved.Failed:N0} fehlgeschlagen.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.IO.IOException)
        {
            StatusText = ex.Message;
            _logger.LogWarning(ex, "Ablegen auf Ordner {FolderId} fehlgeschlagen", folderId);
        }
    }

    /// <summary>Dateien auf ein Album abgelegt.</summary>
    public async Task DropOnAlbumAsync(AlbumFilterEntry entry, IReadOnlyList<int> fileIds)
    {
        if (entry.IsSmart)
        {
            StatusText = $"„{entry.Name}“ ist ein intelligentes Album und füllt sich selbst über seinen Filter.";
            return;
        }

        await Gallery.AddToAlbumAsync(entry.AlbumId, entry.Name, fileIds);
        StatusText = $"{fileIds.Count:N0} zu „{entry.Name}“ hinzugefügt (verknüpft, nicht kopiert).";
    }
}
