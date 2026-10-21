using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using PhotoViewerV3.Services.Library;

namespace PhotoViewerV3.Libraries.Linking;

/// <summary>Bibliothek, mit der verknüpft werden kann (Auswahlliste im Dialog).</summary>
public sealed record LibraryLinkCandidate(int Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>Anfrage an den Dialog „Verknüpfen mit…“ (ADR-0048).</summary>
public sealed record LibraryLinkDialogRequest(int LibraryId, string LibraryName, IReadOnlyList<LibraryLinkCandidate> Candidates);

/// <summary>Bestätigte Wahl: Partner-Bibliothek und gleichnamige Personen, die getrennt bleiben sollen.</summary>
public sealed record LibraryLinkDialogResult(int OtherLibraryId, IReadOnlyList<int> KeepApartSourcePersonIds);

/// <summary>Zeigt den Dialog „Verknüpfen mit…“; null = abgebrochen.</summary>
public interface ILibraryLinkDialogService
{
    Task<LibraryLinkDialogResult?> ShowLinkAsync(LibraryLinkDialogRequest request);
}

/// <summary>Eine gleichnamige Person im Dialog; Standard: zusammenführen.</summary>
public sealed partial class LibraryLinkPersonChoice : ObservableObject
{
    public LibraryLinkPersonChoice(LibraryLinkPersonConflict conflict) => Conflict = conflict;

    public LibraryLinkPersonConflict Conflict { get; }

    public string Name => Conflict.Name;

    [ObservableProperty]
    private bool _merge = true;
}

/// <summary>
/// Logik des Dialogs „Verknüpfen mit…“ (ADR-0048): Partner wählen, Vorschau laden (was zusammengeführt wird, was
/// wandert), gleichnamige Personen einzeln zusammenführen oder getrennt halten. Ohne Fenster testbar.
/// </summary>
public sealed partial class LibraryLinkDialogViewModel : ObservableObject
{
    private readonly ILibraryLinkService _linking;
    private int _previewVersion;

    public LibraryLinkDialogViewModel(ILibraryLinkService linking, LibraryLinkDialogRequest request)
    {
        _linking = linking;
        Request = request;
        Candidates = request.Candidates;
    }

    public LibraryLinkDialogRequest Request { get; }

    public IReadOnlyList<LibraryLinkCandidate> Candidates { get; }

    public string Title => $"„{Request.LibraryName}“ verknüpfen";

    public string Intro =>
        $"Verknüpfte Bibliotheken teilen Tags, Kategorien, Personen und Beziehungen und lassen sich unter „Alle verknüpften“ gemeinsam ansehen. "
        + $"Die Gruppe behält den Namen von „{Request.LibraryName}“. Dateien und Ordner bleiben, wo sie sind.";

    public ObservableCollection<LibraryLinkPersonChoice> PersonChoices { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    private LibraryLinkCandidate? _selectedCandidate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    private LibraryLinkPreview? _preview;

    [ObservableProperty]
    private string _summaryText = "Bitte eine Bibliothek wählen.";

    [ObservableProperty]
    private string? _errorText;

    public bool HasPersonChoices => PersonChoices.Count > 0;

    public bool CanConfirm => SelectedCandidate is not null && Preview is not null && Preview.OtherLibraryId == SelectedCandidate.Id;

    partial void OnSelectedCandidateChanged(LibraryLinkCandidate? value) => _ = LoadPreviewAsync(value);

    /// <summary>Vorschau für den gewählten Partner laden (veraltete Antworten werden verworfen).</summary>
    public async Task LoadPreviewAsync(LibraryLinkCandidate? candidate)
    {
        var version = ++_previewVersion;
        Preview = null;
        PersonChoices.Clear();
        OnPropertyChanged(nameof(HasPersonChoices));
        ErrorText = null;
        if (candidate is null)
        {
            SummaryText = "Bitte eine Bibliothek wählen.";
            return;
        }

        SummaryText = "Vorschau wird berechnet…";
        try
        {
            var preview = await _linking.PreviewLinkAsync(Request.LibraryId, candidate.Id).ConfigureAwait(true);
            if (version != _previewVersion)
            {
                return;
            }

            foreach (var conflict in preview.PersonConflicts)
            {
                PersonChoices.Add(new LibraryLinkPersonChoice(conflict));
            }

            OnPropertyChanged(nameof(HasPersonChoices));
            SummaryText = BuildSummary(preview);
            Preview = preview;
        }
        catch (Exception ex)
        {
            if (version == _previewVersion)
            {
                SummaryText = string.Empty;
                ErrorText = ex.Message;
            }
        }
    }

    /// <summary>Ergebnis für die Bestätigung; null, solange keine Vorschau vorliegt.</summary>
    public LibraryLinkDialogResult? BuildResult()
        => CanConfirm
            ? new LibraryLinkDialogResult(SelectedCandidate!.Id, PersonChoices.Where(p => !p.Merge).Select(p => p.Conflict.SourcePersonId).ToList())
            : null;

    /// <summary>Zusammenfassung für den Bestätigungsdialog (auch für Tests).</summary>
    public static string BuildSummary(LibraryLinkPreview preview)
    {
        var sb = new StringBuilder();
        sb.Append($"„{preview.OtherLibraryName}“ kommt in die Gruppe von „{preview.LibraryName}“.");
        sb.AppendLine();
        sb.Append($"Tags: {Pair(preview.TagsMerged, preview.TagsMoved)} · Kategorien: {Pair(preview.CategoriesMerged, preview.CategoriesMoved)} · ");
        sb.Append($"Beziehungen: {Pair(preview.RelationsMerged, preview.RelationsMoved)} · Personen: {preview.PersonsMoved} kommen hinzu");
        if (preview.PersonConflicts.Count > 0)
        {
            sb.AppendLine();
            sb.Append($"{preview.PersonConflicts.Count} gleichnamige Person(en) — unten wählen, ob sie zusammengeführt werden (Standard) "
                      + "oder getrennt bleiben (sie bekommen dann den Bibliotheksnamen als Rufnamen-Zusatz).");
        }

        return sb.ToString();

        static string Pair(int merged, int moved) => merged == 0
            ? $"{moved} kommen hinzu"
            : $"{merged} gleichnamige werden eins, {moved} kommen hinzu";
    }
}
