using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using PhotoViewerV3.Services.Library;
using PhotoViewerV3.Services.Messaging;

namespace PhotoViewerV3.Shell;

/// <summary>Eine Bibliothek im Umschalter oben in der Navigation.</summary>
public sealed record LibrarySwitchItem(int Id, string Name, int FileCount, bool IsActive)
{
    /// <summary>Zweite Zeile im Menü („1.248 Dateien“).</summary>
    public string DetailText => FileCount == 1 ? "1 Datei" : $"{FileCount:N0} Dateien";
}

/// <summary>
/// Bibliotheks-Umschalter oben in der linken Leiste (ADR-0042): zeigt die aktive Bibliothek, wechselt per Menü zu einer
/// anderen (ILibraryService.SetActiveAsync → ActiveLibraryChangedMessage, Galerie/Ordner laden neu) und springt mit
/// „Bibliotheken verwalten…“ in Einstellungen › Bibliotheken &amp; Eingänge. Ohne Bibliothek führt der Knopf direkt dorthin.
/// Liest neu bei Bibliothekswechsel, nach Scans, beim Öffnen des Menüs und beim Verlassen der Einstellungen (dort wird
/// angelegt und umbenannt) — nicht bei jeder Navigation, weil die Zählung über alle Dateien geht.
/// </summary>
public sealed partial class LibrarySwitcherViewModel : ObservableObject
{
    private readonly ILibraryService _libraries;
    private readonly INavigationService _navigation;
    private readonly IDialogService _dialogs;
    private readonly ILogger<LibrarySwitcherViewModel> _logger;
    private readonly Libraries.IInboxActions? _libraryManagement;
    private Task? _running;
    private bool _refreshAgain;
    private string? _lastTag;

    /// <param name="messenger">Optional (Tests); produktiv WeakReferenceMessenger.Default.</param>
    /// <param name="libraryManagement">Bibliotheksverwaltung: während ihres Scans wird nicht gewechselt (wie „Aktiv setzen“).</param>
    public LibrarySwitcherViewModel(
        ILibraryService libraries,
        INavigationService navigation,
        IDialogService dialogs,
        ILogger<LibrarySwitcherViewModel> logger,
        IMessenger? messenger = null,
        Libraries.IInboxActions? libraryManagement = null,
        HiddenLibraries.HiddenLibrariesViewModel? hidden = null)
    {
        Hidden = hidden; // versteckte Bibliotheken, nur mit „_hidden“ (ADR-0049)
        _libraries = libraries;
        _navigation = navigation;
        _dialogs = dialogs;
        _logger = logger;
        _libraryManagement = libraryManagement;

        var bus = messenger ?? WeakReferenceMessenger.Default;
        bus.Register<LibrarySwitcherViewModel, ActiveLibraryChangedMessage>(this, static (r, m) => UiThread.Post(() => { _ = r.RefreshAsync(); }));
        bus.Register<LibrarySwitcherViewModel, ScanCompletedMessage>(this, static (r, m) => UiThread.Post(() => { _ = r.RefreshAsync(); }));
        _navigation.Navigated += OnNavigated;
    }

    /// <summary>Alle Bibliotheken, die aktive markiert (Reihenfolge wie in den Einstellungen).</summary>
    public ObservableCollection<LibrarySwitchItem> Libraries { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CaptionText))]
    private bool _hasLibrary;

    /// <summary>Name der aktiven Bibliothek bzw. „Keine Bibliothek“.</summary>
    [ObservableProperty]
    private string _activeName = "Keine Bibliothek";

    /// <summary>Zweite Zeile unter dem Namen.</summary>
    public string CaptionText => Hidden is { IsHiddenSessionActive: true }
        ? "Versteckte Bibliothek"
        : HasLibrary ? "Bibliothek wechseln" : "Bibliothek einrichten";

    /// <summary>Versteckte Bibliotheken (ADR-0049); null bzw. ohne Startschalter ohne Wirkung.</summary>
    public HiddenLibraries.HiddenLibrariesViewModel? Hidden { get; }

    /// <summary>Für das Menü: true, wenn es eine Bibliothek zum Umschalten gibt (sonst führt der Klick zu „verwalten“).</summary>
    public bool HasChoices => Libraries.Any() || Hidden is { IsHiddenModeEnabled: true };

    /// <summary>
    /// Liest Bibliotheken und aktive Bibliothek neu. Läuft schon ein Lesen, wird es danach wiederholt und dessen Task
    /// geliefert — wer wartet, sieht also immer einen Stand nach seinem Aufruf.
    /// </summary>
    public Task RefreshAsync()
    {
        if (_running is { IsCompleted: false })
        {
            _refreshAgain = true;
            return _running;
        }

        _running = RunRefreshAsync();
        return _running;
    }

    private async Task RunRefreshAsync()
    {
        try
        {
            do
            {
                _refreshAgain = false;
                var all = await _libraries.GetAllAsync().ConfigureAwait(true);
                var active = await _libraries.GetActiveAsync().ConfigureAwait(true);
                Libraries.Clear();
                foreach (var library in all)
                {
                    Libraries.Add(new LibrarySwitchItem(library.Id, library.Name, library.FileCount, library.Id == active?.Id));
                }

                HasLibrary = active is not null;
                ActiveName = active?.Name ?? "Keine Bibliothek";
            }
            while (_refreshAgain);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Bibliotheks-Umschalter: Bibliotheken nicht lesbar");
        }
    }

    private void OnNavigated(object? sender, string tag)
    {
        var leftSettings = _lastTag == NavigationTags.Settings && tag != NavigationTags.Settings;
        _lastTag = tag;
        if (leftSettings)
        {
            _ = RefreshAsync();
        }
    }

    /// <summary>Andere Bibliothek aktiv setzen; die aktive bleibt unverändert. Nicht während eines Scans der Verwaltung.</summary>
    [RelayCommand]
    private async Task SwitchToAsync(LibrarySwitchItem? item)
    {
        if (item is null || item.IsActive)
        {
            return;
        }

        if (_libraryManagement is { IsScanning: true })
        {
            await _dialogs.ShowErrorAsync("Bibliothek wechseln", "Gerade läuft ein Scan. Bitte warten, bis er fertig ist.").ConfigureAwait(true);
            return;
        }

        try
        {
            _logger.LogInformation("Bibliotheks-Umschalter: wechsle zu Bibliothek {Id}", item.Id);
            await _libraries.SetActiveAsync(item.Id).ConfigureAwait(true);
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Bibliotheks-Umschalter: Wechsel zu {Id} fehlgeschlagen", item.Id);
            await _dialogs.ShowErrorAsync("Bibliothek wechseln", $"„{item.Name}“ konnte nicht aktiviert werden: {ex.Message}").ConfigureAwait(true);
        }
    }

    /// <summary>„Bibliotheken verwalten…“ → Einstellungen › Bibliotheken &amp; Eingänge.</summary>
    [RelayCommand]
    private void Manage() => _navigation.NavigateTo(NavigationTags.Library);
}
