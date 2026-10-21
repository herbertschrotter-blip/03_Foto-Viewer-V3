using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Lumora.Faces;
using Lumora.Services.Faces.Erase;
using Lumora.Services.Library;
using Lumora.Shell;

namespace Lumora.Settings.FaceData;

/// <summary>
/// Einstellungen → Gesichtserkennung → „Gesichtsdaten löschen…“ (0.2.38): nach Bestätigung im Dialog
/// (<see cref="IDialogService"/> → AppDialogWindow) löscht <see cref="IFaceDataEraser"/> Personen, Gesichter,
/// Fingerabdrücke, Ablehnungen und den Gesichter-Cache der aktiven Bibliothek und schaltet den Index aus. Fotos bleiben.
/// </summary>
public sealed partial class FaceDataSettingsViewModel : ObservableObject
{
    /// <summary>Titel des Bestätigungsdialogs.</summary>
    public const string ConfirmTitle = "Gesichtsdaten löschen?";

    /// <summary>Hinweis unter dem Knopf.</summary>
    public const string PrivacyText = "Alles bleibt auf diesem PC — Fotos und Gesichtsdaten verlassen den Rechner nicht.";

    private readonly IFaceDataEraser _eraser;
    private readonly ILibraryService _libraries;
    private readonly IDialogService _dialogs;
    private readonly FaceSettingsViewModel? _faceSettings;
    private readonly ILogger<FaceDataSettingsViewModel> _logger;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EraseCommand))]
    private bool _isErasing;

    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _messageIsError;

    public FaceDataSettingsViewModel(
        IFaceDataEraser eraser,
        ILibraryService libraries,
        IDialogService dialogs,
        ILogger<FaceDataSettingsViewModel> logger,
        FaceSettingsViewModel? faceSettings = null)
    {
        _eraser = eraser;
        _libraries = libraries;
        _dialogs = dialogs;
        _logger = logger;
        _faceSettings = faceSettings;
    }

    /// <summary>Text des Bestätigungsdialogs für die Bibliothek.</summary>
    public static string ConfirmMessage(string libraryName)
        => $"Für die Bibliothek „{libraryName}“ werden gelöscht:\n" +
           "• alle erkannten Gesichter mit ihren Fingerabdrücken,\n" +
           "• die Zuordnungen zu Personen und abgelehnte Vorschläge,\n" +
           "• Personen, die danach in keiner Bibliothek mehr vorkommen,\n" +
           "• der Gesichter-Cache (Ausschnitte für die Personen-Seite).\n\n" +
           "Die Fotos selbst, Bewertungen, Tags und Kategorien bleiben unverändert. Die Gesichtserkennung wird ausgeschaltet; " +
           "schaltest du sie wieder ein, werden die Fotos neu analysiert — benannte Personen müssen dann neu benannt werden.\n\n" +
           "Das lässt sich nicht rückgängig machen.";

    private bool CanErase() => !IsErasing;

    /// <summary>„Gesichtsdaten löschen…“: Bestätigung, dann löschen.</summary>
    [RelayCommand(CanExecute = nameof(CanErase))]
    private async Task EraseAsync()
    {
        var library = await _libraries.GetActiveAsync().ConfigureAwait(true);
        if (library is null)
        {
            ShowMessage("Keine Bibliothek ausgewählt.", error: true);
            return;
        }

        var ok = await _dialogs.ConfirmAsync(ConfirmTitle, ConfirmMessage(library.Name), "Gesichtsdaten löschen", isDestructive: true).ConfigureAwait(true);
        if (!ok)
        {
            return;
        }

        await EraseConfirmedAsync(library.Id).ConfigureAwait(true);
    }

    /// <summary>Löschen ohne Rückfrage (nach der Bestätigung; Rauchtest).</summary>
    internal async Task EraseConfirmedAsync(int libraryId)
    {
        IsErasing = true;
        Message = null;
        try
        {
            var result = await _eraser.EraseLibraryAsync(libraryId).ConfigureAwait(true);
            ShowMessage(
                $"Gesichtsdaten gelöscht: {result.Faces:N0} Gesichter, {result.Persons:N0} Personen, {result.Rejections:N0} Ablehnungen, " +
                $"{result.CacheFiles:N0} Cache-Dateien. Die Gesichtserkennung ist ausgeschaltet.",
                error: false);
        }
        catch (Exception ex)
        {
            ShowMessage($"Gesichtsdaten konnten nicht gelöscht werden: {ex.Message}", error: true);
            _logger.LogError(ex, "Gesichtsdaten löschen fehlgeschlagen");
        }
        finally
        {
            IsErasing = false;
            _faceSettings?.Load(); // Schalter „Gesichter im Hintergrund erkennen“ zeigt wieder aus
        }
    }

    private void ShowMessage(string text, bool error)
    {
        MessageIsError = error;
        Message = text;
    }
}
