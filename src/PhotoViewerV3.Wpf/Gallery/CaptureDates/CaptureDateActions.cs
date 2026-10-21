using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.Logging;
using PhotoViewerV3.Folders;
using PhotoViewerV3.Services.Metadata;

namespace PhotoViewerV3.Gallery.CaptureDates;

/// <summary>Zeigt den Dialog „Aufnahmedatum setzen“ (austauschbar: Rauchtest-Fake).</summary>
public interface ICaptureDateDialogService
{
    /// <summary>Dialog modal zeigen; null bei Abbruch.</summary>
    Task<CaptureDateDialogResult?> PickAsync(CaptureDateDialogRequest request, Window? owner);
}

/// <inheritdoc cref="ICaptureDateDialogService"/>
public sealed class CaptureDateDialogService : ICaptureDateDialogService
{
    public Task<CaptureDateDialogResult?> PickAsync(CaptureDateDialogRequest request, Window? owner)
        => Task.FromResult(SetCaptureDateDialog.Show(owner ?? Application.Current?.MainWindow, request));
}

/// <summary>
/// „Datum setzen…“ (ADR-0046) für Galerie-Auswahl und Ordnerbaum (alle Dateien darin, wahlweise mit Unterordnern):
/// Dialog zeigen, Änderung über <see cref="ICaptureDateService"/> schreiben, Ergebnis als Text. Schreibt nur den Katalog.
/// </summary>
public sealed class CaptureDateActions
{
    private readonly ICaptureDateDialogService _dialog;
    private readonly ILogger<CaptureDateActions> _logger;

    public CaptureDateActions(ICaptureDateService service, ICaptureDateDialogService dialog, ILogger<CaptureDateActions> logger)
    {
        Service = service;
        _dialog = dialog;
        _logger = logger;
    }

    public ICaptureDateService Service { get; }

    /// <summary>Für ausgewählte Dateien; liefert den Ergebnistext oder null bei Abbruch.</summary>
    public async Task<string?> SetForFilesAsync(IReadOnlyList<int> fileIds, Window? owner)
    {
        if (fileIds.Count == 0)
        {
            return null;
        }

        var current = await Service.GetAsync(fileIds[0]).ConfigureAwait(true);
        var subject = fileIds.Count == 1 ? "Ausgewählte Datei" : "Auswahl";
        var choice = await _dialog.PickAsync(new CaptureDateDialogRequest(subject, fileIds.Count, null, current), owner).ConfigureAwait(true);
        if (choice is null)
        {
            return null;
        }

        var result = await Service.ApplyAsync(fileIds, choice.Edit).ConfigureAwait(true);
        return Describe(result);
    }

    /// <summary>Für alle Dateien eines Ordners (Dialog fragt „Unterordner einschließen“); null bei Abbruch.</summary>
    public async Task<FolderActionResult?> SetForFolderAsync(int folderId, string folderName, Window? owner)
    {
        var direct = await Service.GetFileIdsInFolderAsync(folderId, includeSubfolders: false).ConfigureAwait(true);
        var all = await Service.GetFileIdsInFolderAsync(folderId, includeSubfolders: true).ConfigureAwait(true);
        if (all.Count == 0)
        {
            return new FolderActionResult($"„{folderName}“ enthält keine Dateien — kein Datum zu setzen.", true);
        }

        var current = await Service.GetAsync(direct.Count > 0 ? direct[0] : all[0]).ConfigureAwait(true);
        var request = new CaptureDateDialogRequest($"Ordner „{folderName}“", direct.Count, all.Count, current);
        var choice = await _dialog.PickAsync(request, owner).ConfigureAwait(true);
        if (choice is null)
        {
            return null;
        }

        var ids = choice.IncludeSubfolders ? all : direct;
        if (ids.Count == 0)
        {
            return new FolderActionResult($"„{folderName}“ enthält direkt keine Dateien — „Unterordner einschließen“ wählen.", true);
        }

        var result = await Service.ApplyAsync(ids, choice.Edit).ConfigureAwait(true);
        _logger.LogInformation("Datum setzen für Ordner {FolderId}: {Changed} geändert", folderId, result.Changed);
        return new FolderActionResult($"„{folderName}“: {Describe(result)}", false);
    }

    /// <summary>Ergebnistext („Aufnahmedatum ca. 1982: 12 Datei(en) geändert — Rückgängig möglich.“).</summary>
    public static string Describe(CaptureDateChangeResult result)
        => result.Changed == 0
            ? $"{result.Description}: keine Änderung (alle {result.Unchanged:N0} Datei(en) hatten diesen Wert schon)."
            : $"{result.Description}: {result.Changed:N0} Datei(en) geändert — Rückgängig möglich.";
}
