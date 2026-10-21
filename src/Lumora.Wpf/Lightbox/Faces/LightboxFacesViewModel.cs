using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Gallery.ListView;
using Lumora.Services.Faces.Assignment;
using Lumora.Services.Faces.Indexing;
using Lumora.Shell;

namespace Lumora.Lightbox.Faces;

/// <summary>
/// Gesichter in der Lightbox (0.2.38, Gesichtserkennung Teil 4): Umschalter „Gesichter“ (Taste F) zeigt Rahmen über den
/// erkannten Gesichtern des aktuellen Fotos, beschriftet mit Name / „Vorschlag: X?“ / „Unbekannt“. Klick auf einen
/// Rahmen öffnet ein Menü: „Ist X“ (bestätigen), „Nicht X“ (ablehnen), „Ist …“ (Person wählen oder neu anlegen),
/// „Kein Gesicht“ (ignorieren). Alle Lese- und Schreibzugriffe laufen über <see cref="IFaceRecognitionService"/>;
/// danach werden die Rahmen neu geladen (auch auf <see cref="PersonsChangedMessage"/>/<see cref="FacesChangedMessage"/>).
/// Ohne Dienst (Tests) bleibt der Umschalter wirkungslos. Eigene Klasse statt Teil des LightboxViewModel, damit sie
/// ohne LibVLC testbar ist.
/// </summary>
public sealed partial class LightboxFacesViewModel : ObservableObject, IDisposable
{
    /// <summary>So viele Personen stehen direkt im Untermenü „Ist …“ (nach Anzahl Gesichter), der Rest über die Eingabe.</summary>
    public const int MenuPersonLimit = 12;

    private readonly IFaceRecognitionService? _service;
    private readonly IDialogService? _dialogs;
    private readonly ILogger _logger;
    private readonly IMessenger _messenger;
    private readonly Dispatcher _dispatcher;
    private int _loadVersion;
    private int? _fileId;
    private bool _isImage;
    private bool _disposed;

    public LightboxFacesViewModel(
        IFaceRecognitionService? service,
        IDialogService? dialogs = null,
        ILogger? logger = null,
        IMessenger? messenger = null,
        Dispatcher? dispatcher = null,
        IGalleryViewSettingsStore? settings = null)
    {
        _settings = settings; // Zustand der Taste F merken (0.2.39, LightboxFacesViewModel.Persistence.cs)
        _service = service;
        _dialogs = dialogs;
        _logger = logger ?? NullLogger.Instance;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
        _dispatcher = dispatcher ?? Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        if (service is not null)
        {
            _messenger.Register<LightboxFacesViewModel, PersonsChangedMessage>(this, static (r, _) => r.Post(r.ReloadIfVisibleAsync));
            _messenger.Register<LightboxFacesViewModel, FacesChangedMessage>(this, static (r, m) =>
            {
                if (r._fileId is { } id && (m.FileIds.Count == 0 || m.FileIds.Contains(id)))
                {
                    r.Post(r.ReloadIfVisibleAsync);
                }
            });
        }
    }

    /// <summary>Statusmeldung für die Lightbox (Hinweis unten), vom LightboxViewModel gesetzt.</summary>
    public Action<string>? ShowStatus { get; set; }

    /// <summary>True, wenn der Zuordnungsdienst vorhanden ist.</summary>
    public bool IsAvailable => _service is not null;

    /// <summary>Rahmen sichtbar (Taste F); bleibt beim Blättern an.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBoxes))]
    private bool _isVisible;

    /// <summary>Rahmen werden gezeichnet: eingeschaltet und ein Foto (kein Video/Archiv) wird gezeigt.</summary>
    public bool ShowBoxes => IsVisible && _isImage;

    /// <summary>Rahmen des aktuellen Fotos (ohne ignorierte Gesichter).</summary>
    public ObservableCollection<LightboxFaceBox> Boxes { get; } = new();

    /// <summary>Aktuelle Datei; mit eingeschalteten Rahmen werden sie sofort geladen.</summary>
    public Task SetFileAsync(int? fileId, bool isImage)
    {
        _fileId = fileId;
        _isImage = isImage;
        _loadVersion++; // ein noch laufendes Laden der vorigen Datei darf hier nichts mehr eintragen
        OnPropertyChanged(nameof(ShowBoxes));
        Boxes.Clear();
        return IsVisible ? LoadAsync() : Task.CompletedTask;
    }

    /// <summary>Taste F bzw. Knopf „Gesichter“.</summary>
    public async Task ToggleAsync()
    {
        if (_service is null)
        {
            ShowStatus?.Invoke("Gesichtserkennung nicht verfügbar.");
            return;
        }

        IsVisible = !IsVisible;
        _toggledByUser = true; // ein noch laufendes Wiederherstellen (RestoreAsync) überschreibt die Taste nicht mehr
        if (!IsVisible)
        {
            _loadVersion++;
            Boxes.Clear();
            await SaveVisibilityAsync().ConfigureAwait(true);
            return;
        }

        await SaveVisibilityAsync().ConfigureAwait(true);
        if (!IsVisible)
        {
            return; // während des Speicherns schon wieder ausgeschaltet
        }

        if (!_isImage)
        {
            ShowStatus?.Invoke("Gesichter gibt es nur bei Fotos.");
            return;
        }

        await LoadAsync().ConfigureAwait(true);
        ShowStatus?.Invoke(Boxes.Count == 0
            ? "Keine Gesichter — noch nicht analysiert oder keine erkannt (Einstellungen → Gesichtserkennung)."
            : Boxes.Count == 1 ? "1 Gesicht — Klick auf den Rahmen zum Benennen." : $"{Boxes.Count} Gesichter — Klick auf einen Rahmen zum Benennen.");
    }

    private Task ReloadIfVisibleAsync() => IsVisible ? LoadAsync() : Task.CompletedTask;

    /// <summary>Rahmen der aktuellen Datei laden; ältere Ladevorgänge (schnelles Blättern) werden verworfen.</summary>
    public async Task LoadAsync()
    {
        var version = ++_loadVersion;
        if (_service is null || _fileId is not { } fileId || !_isImage)
        {
            Boxes.Clear();
            return;
        }

        try
        {
            var map = await _service.GetFacesForFilesAsync(new[] { fileId }, CancellationToken.None).ConfigureAwait(true);
            if (_disposed || version != _loadVersion)
            {
                return;
            }

            Boxes.Clear();
            if (map.TryGetValue(fileId, out var faces))
            {
                foreach (var face in faces.Where(f => f.State != Domain.Enums.FaceState.Ignored).OrderBy(f => f.BoxRelative.X))
                {
                    Boxes.Add(new LightboxFaceBox(face));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Gesichter für File {FileId} konnten nicht geladen werden", fileId);
            ShowStatus?.Invoke($"Gesichter konnten nicht geladen werden: {ex.Message}");
        }
    }

    // ---- Menü ------------------------------------------------------------------------------------

    /// <summary>Menü für einen Rahmen: je nach Art bestätigen/ablehnen, Person wählen oder neu anlegen, kein Gesicht.</summary>
    public async Task<IReadOnlyList<FaceMenuItem>> BuildMenuAsync(LightboxFaceBox face)
    {
        ArgumentNullException.ThrowIfNull(face);
        var items = new List<FaceMenuItem>();
        if (face.Kind == LightboxFaceKind.Suggested && face.SuggestedPersonId is { } suggestedId)
        {
            var name = face.SuggestedPersonName ?? "?";
            items.Add(new FaceMenuItem($"Ist {name}", () => ConfirmAsync(face, suggestedId, name)));
            items.Add(new FaceMenuItem($"Nicht {name}", () => RejectAsync(face)));
            items.Add(FaceMenuItem.Separator);
        }

        items.Add(new FaceMenuItem("Ist …", Children: await BuildPersonChoicesAsync(face).ConfigureAwait(true)));
        items.Add(new FaceMenuItem("Kein Gesicht", () => IgnoreAsync(face)));
        return items;
    }

    private async Task<IReadOnlyList<FaceMenuItem>> BuildPersonChoicesAsync(LightboxFaceBox face)
    {
        var choices = new List<FaceMenuItem>();
        IReadOnlyList<PersonSummary> persons = [];
        if (_service is not null)
        {
            try
            {
                persons = await _service.GetPersonsAsync(CancellationToken.None).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Personen für das Gesichter-Menü konnten nicht geladen werden");
            }
        }

        foreach (var person in persons
                     .Where(p => !p.IsHidden && p.Id != face.PersonId)
                     .OrderByDescending(p => p.FaceCount)
                     .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
                     .Take(MenuPersonLimit)
                     .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var id = person.Id;
            var name = person.Name;
            choices.Add(new FaceMenuItem(name, () => ConfirmAsync(face, id, name)));
        }

        if (choices.Count > 0)
        {
            choices.Add(FaceMenuItem.Separator);
        }

        choices.Add(new FaceMenuItem(choices.Count > 0 ? "Andere oder neue Person…" : "Neue Person…", () => PromptAndAssignAsync(face)));
        return choices;
    }

    // ---- Aktionen --------------------------------------------------------------------------------

    /// <summary>„Ist X“: Gesicht der Person zuordnen (bestätigt).</summary>
    public Task ConfirmAsync(LightboxFaceBox face, int personId, string personName)
        => RunAsync(() => _service!.ConfirmAsync(face.FaceId, personId), $"Gesicht als „{personName}“ bestätigt.");

    /// <summary>„Nicht X“: Vorschlag ablehnen (die Person wird für dieses Gesicht nicht mehr vorgeschlagen).</summary>
    public Task RejectAsync(LightboxFaceBox face)
        => RunAsync(() => _service!.RejectSuggestionAsync(face.FaceId), $"Vorschlag „{face.SuggestedPersonName}“ abgelehnt.");

    /// <summary>„Kein Gesicht“: Erkennung ignorieren (der Rahmen verschwindet).</summary>
    public Task IgnoreAsync(LightboxFaceBox face)
        => RunAsync(() => _service!.IgnoreFaceAsync(face.FaceId), "Als „kein Gesicht“ markiert.");

    /// <summary>
    /// „Ist …“ mit Namen: passt der Name (ohne Groß-/Kleinschreibung) zu einer vorhandenen Person, wird ihr zugeordnet,
    /// sonst entsteht eine neue Person. Leerer Name → nichts.
    /// </summary>
    public async Task AssignByNameAsync(LightboxFaceBox face, string name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0 || _service is null)
        {
            return;
        }

        PersonSummary? existing = null;
        try
        {
            var persons = await _service.GetPersonsAsync(CancellationToken.None).ConfigureAwait(true);
            existing = persons.FirstOrDefault(p => string.Equals(p.Name, trimmed, StringComparison.CurrentCultureIgnoreCase));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Personen konnten nicht geladen werden");
        }

        if (existing is not null)
        {
            await ConfirmAsync(face, existing.Id, existing.Name).ConfigureAwait(true);
            return;
        }

        await RunAsync(() => _service.ConfirmNewPersonAsync(face.FaceId, trimmed), $"Neue Person „{trimmed}“ angelegt und bestätigt.").ConfigureAwait(true);
    }

    private async Task PromptAndAssignAsync(LightboxFaceBox face)
    {
        if (_dialogs is null)
        {
            ShowStatus?.Invoke("Eingabe nicht verfügbar.");
            return;
        }

        var initial = face.Kind == LightboxFaceKind.Suggested ? face.SuggestedPersonName ?? string.Empty : string.Empty;
        var name = await _dialogs.PromptTextAsync(
            "Wer ist das?",
            "Name der Person — eine vorhandene Person mit diesem Namen wird zugeordnet, sonst wird sie neu angelegt.",
            initial,
            "Zuordnen").ConfigureAwait(true);
        if (name is not null)
        {
            await AssignByNameAsync(face, name).ConfigureAwait(true);
        }
    }

    private async Task RunAsync(Func<Task> action, string doneText)
    {
        if (_service is null)
        {
            return;
        }

        try
        {
            await action().ConfigureAwait(true);
            ShowStatus?.Invoke(doneText);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Gesichter: Aktion fehlgeschlagen");
            ShowStatus?.Invoke($"Nicht gespeichert: {ex.Message}");
        }

        await LoadAsync().ConfigureAwait(true);
    }

    private void Post(Func<Task> work)
    {
        if (_disposed)
        {
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            _ = work();
        }
        else if (!_dispatcher.HasShutdownStarted)
        {
            _dispatcher.InvokeAsync(() => _ = work());
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _messenger.UnregisterAll(this);
    }
}
