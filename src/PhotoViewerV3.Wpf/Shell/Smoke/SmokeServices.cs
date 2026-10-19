using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using PhotoViewerV3.Gallery.Services;

namespace PhotoViewerV3.Shell.Smoke;

/// <summary>
/// Rauchtest-Ersatz für die Galerie-Dialoge: Bestätigungen (Papierkorb) werden automatisch mit „Ja“
/// beantwortet, Fehlerlisten nur geloggt — ein echter Dialog würde den unbeaufsichtigten Lauf blockieren.
/// </summary>
internal sealed class SmokeGalleryDialogService : IGalleryDialogService
{
    private readonly ILogger<SmokeGalleryDialogService> _logger;

    public SmokeGalleryDialogService(ILogger<SmokeGalleryDialogService> logger)
    {
        _logger = logger;
    }

    public int Confirmations { get; private set; }

    public Task<bool> ConfirmAsync(string title, string message, string primaryButtonText, bool isDestructive = false)
    {
        Confirmations++;
        _logger.LogInformation("Rauchtest-Dialog „{Title}“: {Message} → automatisch „{Button}“", title, message, primaryButtonText);
        return Task.FromResult(true);
    }

    public Task ShowErrorsAsync(string title, string message, IReadOnlyList<string> details)
    {
        _logger.LogWarning("Rauchtest-Fehlerdialog „{Title}“: {Message} — {Details}", title, message, string.Join(" | ", details));
        return Task.CompletedTask;
    }
}

/// <summary>Rauchtest-Ersatz für den Dialog „Ins Archiv…“: liefert den vom Szenario gesetzten Zielordner und merkt sich die Anfrage.</summary>
internal sealed class SmokeArchiveTargetPicker : PhotoViewerV3.Gallery.ArchiveTarget.IArchiveTargetPicker
{
    private readonly ILogger<SmokeArchiveTargetPicker> _logger;

    public SmokeArchiveTargetPicker(ILogger<SmokeArchiveTargetPicker> logger)
    {
        _logger = logger;
    }

    /// <summary>Nächstes Ergebnis; null = Benutzer bricht ab.</summary>
    public string? NextTarget { get; set; }

    public PhotoViewerV3.Gallery.ArchiveTarget.ArchiveTargetRequest? LastRequest { get; private set; }

    public Task<string?> PickAsync(PhotoViewerV3.Gallery.ArchiveTarget.ArchiveTargetRequest request)
    {
        LastRequest = request;
        _logger.LogInformation(
            "Rauchtest-Archivdialog: {Count} Datei(en), Archiv {Root}, zuletzt {Last}, Vorschlag {Suggestion} → {Target}",
            request.FileIds.Count, request.ArchiveRootPath, request.LastTargetPath ?? "-", request.SuggestedRelativePath ?? "-", NextTarget ?? "(Abbruch)");
        return Task.FromResult(NextTarget);
    }
}

/// <summary>Rauchtest-Ersatz für die Ordnerauswahl („Verschieben nach…“): liefert den vom Szenario gesetzten Zielordner.</summary>
internal sealed class SmokeFolderPickerService : IFolderPickerService
{
    private readonly ILogger<SmokeFolderPickerService> _logger;

    public SmokeFolderPickerService(ILogger<SmokeFolderPickerService> logger)
    {
        _logger = logger;
    }

    /// <summary>Nächstes Ergebnis von <see cref="PickFolder"/>; null = Benutzer bricht ab.</summary>
    public string? NextFolder { get; set; }

    public string? PickFolder(string title, string? initialDirectory)
    {
        _logger.LogInformation("Rauchtest-Ordnerauswahl „{Title}“ (Start {Initial}) → {Folder}", title, initialDirectory ?? "-", NextFolder ?? "(Abbruch)");
        return NextFolder;
    }
}
