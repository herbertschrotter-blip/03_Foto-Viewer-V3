using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using PhotoViewerV3.Lightbox;
using PhotoViewerV3.Services.Faces.Assignment;
using PhotoViewerV3.Services.Faces.Indexing;
using PhotoViewerV3.Shell;

namespace PhotoViewerV3.Persons.ViewModels;

/// <summary>Ansicht der Personen-Seite.</summary>
public enum PersonsPageMode
{
    Overview = 0,
    Person = 1,
    Group = 2,

    /// <summary>Ignorierte Gesichter (ADR-0040).</summary>
    Ignored = 3,
}

/// <summary>Kachel-Listen der Seite (für den sichtbaren Bereich, den die Ansicht meldet).</summary>
public enum PersonsListKind
{
    Persons,
    Groups,
    Suggestions,
    Confirmed,
    GroupMembers,
    Ignored,
}

/// <summary>
/// Seite „Personen“ (ADR-0037): Übersicht mit benannten Personen und unbenannten Gruppen, Detailansicht einer Person
/// (Vorschläge bestätigen/ablehnen, Gesichter entfernen, Titelbild, Umbenennen, Zusammenführen, Ausblenden, Löschen) und
/// Gruppenansicht (Ausreißer abgewählt, als neue Person benennen, zu Person hinzufügen, ignorieren). Alle Katalog- und
/// Modellarbeit macht <see cref="IFaceRecognitionService"/> auf dem Thread-Pool; Nachrichten kommen über
/// <see cref="UiThread"/> auf den UI-Thread.
/// </summary>
public sealed partial class PersonsViewModel : ObservableObject, IDisposable
{
    private readonly IFaceRecognitionService _service;
    private readonly IFaceIndexService _index;
    private readonly FaceImageProvider _images;
    private readonly IPersonsDialogService _dialogs;
    private readonly ILightboxService _lightbox;
    private readonly INavigationService _navigation;
    private readonly ILogger<PersonsViewModel> _logger;
    private readonly IMessenger _messenger;
    private readonly Dictionary<PersonsListKind, (int First, int Last)> _visible = [];
    private int? _libraryId;
    private bool _refreshPending;
    private bool _isVisible;
    private int _busyDepth;

    public PersonsViewModel(
        IFaceRecognitionService service,
        IFaceIndexService index,
        FaceImageProvider images,
        IPersonsDialogService dialogs,
        ILightboxService lightbox,
        INavigationService navigation,
        ILogger<PersonsViewModel> logger,
        IMessenger? messenger = null,
        IFaceIgnoreService? ignore = null,
        IPersonDetailsService? details = null)
    {
        _ignore = ignore;
        _details = details ?? service as IPersonDetailsService; // ADR-0045; im Programm derselbe Dienst
        _service = service;
        _index = index;
        _images = images;
        _dialogs = dialogs;
        _lightbox = lightbox;
        _navigation = navigation;
        _logger = logger;
        _messenger = messenger ?? WeakReferenceMessenger.Default;

        _images.ImagesReady += OnImagesReady;
        _messenger.Register<PersonsViewModel, PersonsChangedMessage>(this, static (r, _) => UiThread.Post(r.OnPersonsChanged));
        _messenger.Register<PersonsViewModel, FaceIndexStatusMessage>(this, static (r, m) => UiThread.Post(() => r.ApplyIndexStatus(m.Status)));
        ApplyIndexStatus(_index.Status ?? FaceIndexStatus.Disabled);
    }

    /// <summary>Ansicht soll an den Anfang der Listen scrollen (neue Person/Gruppe geöffnet).</summary>
    public event EventHandler? ScrollToTopRequested;

    public TileCollection<PersonTileViewModel> Persons { get; } = [];

    public TileCollection<GroupTileViewModel> Groups { get; } = [];

    public TileCollection<FaceTileViewModel> Suggestions { get; } = [];

    public TileCollection<FaceTileViewModel> Confirmed { get; } = [];

    public TileCollection<FaceTileViewModel> GroupMembers { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOverview), nameof(IsPersonDetail), nameof(IsGroupDetail), nameof(IsIgnoredView))]
    private PersonsPageMode _mode;

    public bool IsOverview => Mode == PersonsPageMode.Overview;

    public bool IsPersonDetail => Mode == PersonsPageMode.Person;

    public bool IsGroupDetail => Mode == PersonsPageMode.Group;

    [ObservableProperty] private bool _isLoading;

    [ObservableProperty] private bool _isGroupsLoading;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NameGroupCommand), nameof(AddGroupToPersonCommand), nameof(IgnoreGroupSelectionCommand),
        nameof(ConfirmSelectedSuggestionsCommand), nameof(RejectSelectedSuggestionsCommand), nameof(ConfirmVisibleSuggestionsCommand),
        nameof(RejectVisibleSuggestionsCommand), nameof(RemoveSelectedConfirmedCommand), nameof(SetCoverCommand),
        nameof(EditPersonCommand), nameof(MergePersonCommand), nameof(ToggleHiddenCommand), nameof(DeletePersonCommand),
        nameof(ConfirmAllSuggestionsCommand), nameof(RejectAllSuggestionsCommand), nameof(IgnoreWholeGroupCommand), nameof(RestoreIgnoredCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private string? _message;

    public bool HasMessage => !string.IsNullOrEmpty(Message);

    [ObservableProperty] private bool _messageIsError;

    // ---- Kopfzeile ---------------------------------------------------------------------------------------------------

    [ObservableProperty] private string _indexStatusText = string.Empty;

    /// <summary>Gesichtserkennung aus bzw. Modell fehlt → Hinweis mit Weg in die Einstellungen.</summary>
    [ObservableProperty] private bool _isRecognitionOff;

    [ObservableProperty] private string _recognitionOffText = string.Empty;

    // ---- Übersicht ---------------------------------------------------------------------------------------------------

    [ObservableProperty] private bool _showHidden;

    [ObservableProperty] private string _personsSummary = string.Empty;

    [ObservableProperty] private string _groupsSummary = string.Empty;

    [ObservableProperty] private bool _hasPersons;

    [ObservableProperty] private bool _hasGroups;

    private IReadOnlyList<PersonOverview> _allPersons = [];

    partial void OnShowHiddenChanged(bool value) => ApplyPersons();

    /// <summary>Seite wird angezeigt (Loaded) — Übersicht bzw. aktuelle Ansicht neu laden.</summary>
    public async Task ActivateAsync()
    {
        _isVisible = true;
        ApplyIndexStatus(_index.Status ?? FaceIndexStatus.Disabled);
        await RefreshCurrentAsync().ConfigureAwait(true);
    }

    public void Deactivate() => _isVisible = false;

    [RelayCommand]
    private async Task RefreshAsync() => await RefreshCurrentAsync(refreshGroups: true).ConfigureAwait(true);

    private Task RefreshCurrentAsync(bool refreshGroups = false) => Mode switch
    {
        PersonsPageMode.Person when CurrentPerson is { } person => LoadPersonAsync(person.Id, keepState: true),
        PersonsPageMode.Group => Task.CompletedTask, // Gruppe bleibt, bis Herbert entscheidet
        PersonsPageMode.Ignored => LoadIgnoredAsync(keepState: true),
        _ => LoadOverviewAsync(refreshGroups),
    };

    public async Task LoadOverviewAsync(bool refreshGroups = false)
    {
        try
        {
            IsLoading = true;
            _libraryId = await _service.GetActiveLibraryIdAsync().ConfigureAwait(true);
            _allPersons = await _service.GetPersonOverviewsAsync().ConfigureAwait(true);
            ApplyPersons();
            IsLoading = false;

            IsGroupsLoading = true;
            var groups = await _service.GetGroupsAsync(refreshGroups).ConfigureAwait(true);
            Groups.ReplaceAll(groups.Groups.Select(g => new GroupTileViewModel(g)));
            HasGroups = Groups.Count > 0;
            GroupsSummary = groups.Groups.Count == 0
                ? groups.UnknownFaces == 0 ? "Keine unbenannten Gesichter." : $"{N(groups.UnknownFaces)} unbenannte Gesichter, aber keine Gruppe (zu wenige ähnliche)."
                : $"{N(groups.Groups.Count)} Gruppe{(groups.Groups.Count == 1 ? string.Empty : "n")} aus {N(groups.UnknownFaces)} unbenannten Gesichtern"
                  + (groups.Sections > 1 ? $" (in {groups.Sections} Abschnitten gruppiert)" : string.Empty);
            RequestVisible(PersonsListKind.Groups);
            await LoadIgnoredCountAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Personen-Seite: Übersicht nicht geladen");
            ShowError("Personen konnten nicht geladen werden: " + ex.Message);
        }
        finally
        {
            IsLoading = false;
            IsGroupsLoading = false;
            RunPendingRefresh();
        }
    }

    /// <summary>Während des Ladens eingetroffene Änderung (PersonsChangedMessage) nachholen.</summary>
    private void RunPendingRefresh()
    {
        if (_refreshPending && !IsBusy)
        {
            _refreshPending = false;
            UiThread.Post(() => _ = RefreshCurrentAsync());
        }
    }

    private void ApplyPersons()
    {
        // Suche, Sortierung und Gruppierung nach Beziehung: PersonsViewModel.Details.cs (0.2.47)
        var visible = FilterAndSortPersons();
        // Schon geladene Titelbilder übernehmen (Suche/Sortierung/Gruppierung legen die Kacheln neu an)
        var images = Persons.Where(t => t.Image is not null && t.ImageFaceId is not null)
            .GroupBy(t => t.ImageFaceId!.Value).ToDictionary(g => g.Key, g => g.First().Image);
        var tiles = visible.Select(p => new PersonTileViewModel(p)).ToList();
        foreach (var tile in tiles)
        {
            if (tile.ImageFaceId is { } faceId && images.TryGetValue(faceId, out var image))
            {
                tile.Image = image;
            }
        }

        Persons.ReplaceAll(tiles);
        HasPersons = Persons.Count > 0;
        var hidden = _allPersons.Count(p => p.Person.IsHidden);
        PersonsSummary = _allPersons.Count == 0
            ? "Noch keine Personen — unten eine Gruppe öffnen und benennen."
            : $"{N(_allPersons.Count)} Person{(_allPersons.Count == 1 ? string.Empty : "en")}" + (hidden > 0 ? $", davon {N(hidden)} ausgeblendet" : string.Empty)
              + (string.IsNullOrWhiteSpace(PersonSearch) ? string.Empty : $" · {N(Persons.Count)} gefunden");
        BuildRelationGroups(tiles);
        RequestVisible(PersonsListKind.Persons);
    }

    [RelayCommand]
    private void OpenSettings() => _navigation.NavigateTo(NavigationTags.SettingsFaces); // Reiter „Gesichter“ (ADR-0042)

    [RelayCommand]
    private async Task BackAsync()
    {
        Mode = PersonsPageMode.Overview;
        CurrentPerson = null;
        CurrentGroup = null;
        Suggestions.ReplaceAll([]);
        Confirmed.ReplaceAll([]);
        GroupMembers.ReplaceAll([]);
        ClearIgnored();
        await LoadOverviewAsync().ConfigureAwait(true);
    }

    // ---- Index-Status ------------------------------------------------------------------------------------------------

    private void ApplyIndexStatus(FaceIndexStatus status)
    {
        IndexStatusText = DescribeIndex(status);
        IsRecognitionOff = status.State is FaceIndexState.Disabled or FaceIndexState.ModelMissing;
        RecognitionOffText = status.State == FaceIndexState.ModelMissing
            ? "Das Modell der Gesichtserkennung fehlt — in den Einstellungen herunterladen."
            : "Die Gesichtserkennung ist ausgeschaltet. Neue Fotos werden nicht analysiert — in den Einstellungen einschalten.";
    }

    /// <summary>Text der Kopfzeile zum Index (für Tests öffentlich).</summary>
    public static string DescribeIndex(FaceIndexStatus status) => status.State switch
    {
        FaceIndexState.Disabled => "Gesichtserkennung ausgeschaltet.",
        FaceIndexState.ModelMissing => "Modell fehlt.",
        FaceIndexState.Running => $"Analysiert: {N(status.Done)} von {N(status.Total)} Fotos · läuft",
        FaceIndexState.Paused => $"Analysiert: {N(status.Done)} von {N(status.Total)} Fotos · pausiert",
        FaceIndexState.Waiting => $"Analysiert: {N(status.Done)} von {N(status.Total)} Fotos · wartet auf den Katalog",
        FaceIndexState.Error => $"Gesichtserkennung: {status.Message}",
        _ => status.Total > 0 ? $"Alle {N(status.Total)} Fotos analysiert." : "Gesichtserkennung bereit.",
    };

    // ---- Bilder und sichtbarer Bereich ---------------------------------------------------------------------------------

    /// <summary>Die Ansicht meldet den sichtbaren Bereich einer Liste — nur dafür werden Ausschnitte angefordert.</summary>
    public void ReportVisibleRange(PersonsListKind list, int first, int last)
    {
        _visible[list] = (first, last);
        RequestVisible(list);
        OnVisibleRangeReported(list);
    }

    private void RequestVisible(PersonsListKind list)
    {
        if (_libraryId is not { } libraryId || !_visible.TryGetValue(list, out var range))
        {
            return;
        }

        var missing = list switch
        {
            PersonsListKind.Persons => Persons.MissingImages(range.First, range.Last),
            PersonsListKind.Groups => Groups.MissingImages(range.First, range.Last),
            PersonsListKind.Suggestions => Suggestions.MissingImages(range.First, range.Last),
            PersonsListKind.Confirmed => Confirmed.MissingImages(range.First, range.Last),
            PersonsListKind.Ignored => Ignored.MissingImages(range.First, range.Last),
            _ => GroupMembers.MissingImages(range.First, range.Last),
        };
        _images.Request(libraryId, missing);
    }

    private void OnImagesReady(IReadOnlyList<(long FaceId, ImageSource Image)> images)
    {
        Persons.Apply(images);
        Groups.Apply(images);
        Suggestions.Apply(images);
        Confirmed.Apply(images);
        GroupMembers.Apply(images);
        Ignored.Apply(images);
    }

    /// <summary>Sichtbare Kacheln einer Liste (für „alle sichtbaren“).</summary>
    private IReadOnlyList<T> VisibleTiles<T>(TileCollection<T> tiles, PersonsListKind list)
        where T : FaceImageTile
    {
        if (!_visible.TryGetValue(list, out var range) || range.First < 0 || tiles.Count == 0)
        {
            return [];
        }

        var last = Math.Min(range.Last, tiles.Count - 1);
        return range.First > last ? [] : tiles.Skip(range.First).Take(last - range.First + 1).ToList();
    }

    // ---- Änderungen von außen ------------------------------------------------------------------------------------------

    private void OnPersonsChanged()
    {
        if (!_isVisible)
        {
            return;
        }

        if (IsBusy || IsLoading || IsGroupsLoading)
        {
            _refreshPending = true; // nach der laufenden Aktion
            return;
        }

        _ = RefreshCurrentAsync();
    }

    /// <summary>Benutzeraktion: sperrt die Knöpfe, zeigt Fehler, lädt danach die aktuelle Ansicht neu.</summary>
    private async Task RunAsync(string what, Func<Task> action, bool reload = true)
    {
        _busyDepth++;
        IsBusy = true;
        Message = null;
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (PersonNameTakenException ex)
        {
            ShowError(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Personen-Seite: {What} fehlgeschlagen", what);
            ShowError($"{what} fehlgeschlagen: {ex.Message}");
        }
        finally
        {
            if (--_busyDepth == 0)
            {
                IsBusy = false;
            }
        }

        if (reload || _refreshPending)
        {
            _refreshPending = false;
            await RefreshCurrentAsync().ConfigureAwait(true);
        }
    }

    private void ShowError(string message)
    {
        Message = message;
        MessageIsError = true;
    }

    private void ShowInfo(string message)
    {
        Message = message;
        MessageIsError = false;
    }

    [RelayCommand]
    private void CloseMessage() => Message = null;

    /// <summary>Kachel an-/abwählen (Klick in der Ansicht).</summary>
    [RelayCommand]
    private void ToggleTile(FaceTileViewModel? tile)
    {
        if (tile is not null)
        {
            tile.IsSelected = !tile.IsSelected;
        }
    }

    /// <summary>Doppelklick: Foto in der Lightbox öffnen (alle Fotos der aktuellen Liste, je Datei einmal).</summary>
    [RelayCommand]
    private async Task OpenFaceAsync(FaceTileViewModel? tile)
    {
        if (tile is null)
        {
            return;
        }

        var list = Mode switch
        {
            PersonsPageMode.Group => GroupMembers,
            PersonsPageMode.Ignored => Ignored,
            _ => Suggestions.Contains(tile) ? Suggestions : Confirmed,
        };
        var fileIds = list.Select(t => t.FileId).Distinct().ToList();
        try
        {
            var files = await _service.GetFilesAsync(fileIds).ConfigureAwait(true);
            var start = files.ToList().FindIndex(f => f.FileId == tile.FileId);
            if (start < 0)
            {
                ShowError("Das Foto ist nicht mehr im Katalog.");
                return;
            }

            var entries = files.Select(f => new LightboxEntry(f.FileId, f.Path, f.Name, f.MediaType, f.IsFavorite, f.Rating, f.Size, f.ModifiedAtUtc)).ToList();
            _lightbox.Open(entries, start, System.Windows.Application.Current?.MainWindow);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Lightbox aus der Personen-Seite nicht geöffnet");
            ShowError("Foto konnte nicht geöffnet werden: " + ex.Message);
        }
    }

    /// <summary>Bestätigen mit Ausreißer-Prüfung: Rückfrage, wenn welche dabei sind; liefert die Zahl der bestätigten Gesichter.</summary>
    private async Task<int> ConfirmWithCheckAsync(IReadOnlyList<long> faceIds, int? personId, string personName, Func<Task<int>>? createPerson = null)
    {
        if (faceIds.Count == 0)
        {
            return 0;
        }

        var checks = await _service.CheckOutliersAsync(faceIds, personId).ConfigureAwait(true);
        var outliers = checks.Where(c => c.IsOutlier).ToList();
        var ids = faceIds;
        if (outliers.Count > 0)
        {
            var decision = await _dialogs.AskOutliersAsync(personName, outliers, faceIds.Count).ConfigureAwait(true);
            if (decision == OutlierDecision.Cancel)
            {
                return 0;
            }

            if (decision == OutlierDecision.SkipOutliers)
            {
                var skip = outliers.Select(o => o.FaceId).ToHashSet();
                ids = faceIds.Where(id => !skip.Contains(id)).ToList();
            }
        }

        if (ids.Count == 0)
        {
            return 0;
        }

        var target = personId ?? (createPerson is null ? throw new InvalidOperationException("Keine Person.") : await createPerson().ConfigureAwait(true));
        await _service.ConfirmManyAsync(ids, target).ConfigureAwait(true);
        return ids.Count;
    }

    private void SubscribeSelection(IEnumerable<FaceTileViewModel> tiles)
    {
        foreach (var tile in tiles)
        {
            tile.PropertyChanged += OnTilePropertyChanged;
        }
    }

    private void OnTilePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FaceTileViewModel.IsSelected) && !_suppressSelection)
        {
            UpdateSelectionState();
        }
    }

    private bool _suppressSelection;

    /// <summary>Viele Kacheln auf einmal an-/abwählen: Zähler und Knöpfe nur einmal am Ende aktualisieren.</summary>
    private void BatchSelection(System.Action change)
    {
        _suppressSelection = true;
        try
        {
            change();
        }
        finally
        {
            _suppressSelection = false;
            UpdateSelectionState();
        }
    }

    private void UpdateSelectionState()
    {
        SelectedSuggestionCount = Suggestions.Count(t => t.IsSelected);
        SelectedConfirmedCount = Confirmed.Count(t => t.IsSelected);
        GroupSelectedCount = GroupMembers.Count(t => t.IsSelected);
        SelectedIgnoredCount = Ignored.Count(t => t.IsSelected);
        RestoreIgnoredCommand.NotifyCanExecuteChanged();
        ConfirmSelectedSuggestionsCommand.NotifyCanExecuteChanged();
        RejectSelectedSuggestionsCommand.NotifyCanExecuteChanged();
        RemoveSelectedConfirmedCommand.NotifyCanExecuteChanged();
        SetCoverCommand.NotifyCanExecuteChanged();
        NameGroupCommand.NotifyCanExecuteChanged();
        AddGroupToPersonCommand.NotifyCanExecuteChanged();
        IgnoreGroupSelectionCommand.NotifyCanExecuteChanged();
    }

    private void RaiseScrollToTop() => ScrollToTopRequested?.Invoke(this, EventArgs.Empty);

    internal static string N(int value) => value.ToString("N0", CultureInfo.CurrentCulture);

    public void Dispose()
    {
        _images.ImagesReady -= OnImagesReady;
        _messenger.UnregisterAll(this);
    }
}
