using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Lumora.Gallery.Data;
using Lumora.Gallery.People;
using Lumora.Services.Faces.Assignment;
using Lumora.Services.Faces.Indexing;

namespace Lumora.Gallery.ViewModels;

/// <summary>
/// Personen in der Galerie (0.2.38, Gesichtserkennung Teil 4): Filter-Dropdown (Personen mit Anzahl, ODER/UND, „ohne
/// Person“, „mit unbenannten Gesichtern“, Option „auch Vorschläge“), Personen je Datei für Listenspalte und Kachel.
/// Gelesen wird direkt über <see cref="GalleryPageSource"/> (Faces/Persons) — der Zuordnungsdienst wird hier nicht
/// gebraucht; Änderungen kommen über <see cref="PersonsChangedMessage"/> und <see cref="FacesChangedMessage"/>.
/// </summary>
public sealed partial class GalleryViewModel
{
    /// <summary>Personen im Filter-Dropdown (Häkchen + Zähler); ausgeblendete Personen nur, solange sie gewählt sind.</summary>
    public ObservableCollection<PersonFilterEntry> PersonFilterEntries { get; } = new();

    public bool HasPersons => PersonFilterEntries.Count > 0;

    /// <summary>Personen-Filter in der Toolbar: sobald es Personen gibt oder in der Bibliothek Gesichter erkannt wurden.</summary>
    [ObservableProperty] private bool _showPersonFilter;

    private bool _libraryHasFaces;
    private long _personsRequestSequence;

    private void UpdateShowPersonFilter() => ShowPersonFilter = HasPersons || _libraryHasFaces || HasPersonFilter;

    /// <summary>Verknüpfung im Personen-Filter (ODER / UND / ohne Person / unbenannte Gesichter).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPersonFilter), nameof(HasPeopleFilter), nameof(PersonFilterText), nameof(EmptyMessagePersons))]
    private GalleryPersonMode _personFilterMode = GalleryPersonMode.Any;

    /// <summary>„Auch Vorschläge“: vorgeschlagene (unbestätigte) Personen zählen mit. Standard aus.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PersonFilterText))]
    private bool _personFilterIncludeSuggestions;

    public bool HasPersonFilter
        => PersonFilterMode.IgnoresPersonSelection() || PersonFilterEntries.Any(e => e.IsSelected);

    /// <summary>Beschriftung des Filter-Knopfs: „Personen“ oder „Personen: Anna, Bert (UND)“.</summary>
    public string PersonFilterText
    {
        get
        {
            // Seit 0.2.47 hängt der Beziehungs-Teil an („Personen: Anna · Beziehung Schwester“, GalleryViewModel.Relations.cs).
            var person = PersonPartText();
            var relations = RelationFilterEntries.Where(e => e.IsSelected).Select(e => e.Name).ToList();
            var relation = relations.Count == 0 ? null : "Beziehung " + string.Join(" oder ", relations);
            return (person, relation) switch
            {
                (null, null) => "Personen",
                (null, { } r) => $"Personen: {r}{(PersonFilterIncludeSuggestions ? " + Vorschläge" : string.Empty)}",
                ({ } p, null) => $"Personen: {p}",
                ({ } p, { } r) => $"Personen: {p} · {r}",
            };
        }
    }

    /// <summary>Personen-Teil der Beschriftung ohne „Personen: “; null ohne Personen-Teil.</summary>
    private string? PersonPartText()
    {
        var suffix = PersonFilterIncludeSuggestions && HasPersonFilter ? " + Vorschläge" : string.Empty;
        switch (PersonFilterMode)
        {
            case GalleryPersonMode.NoPerson:
                return "ohne Person" + suffix;
            case GalleryPersonMode.UnnamedFaces:
                return "unbenannte Gesichter" + suffix;
            case GalleryPersonMode.AnyFace:
                return "alle Fotos mit Gesichtern"; // „auch Vorschläge“ ändert hier nichts (0.2.39)
        }

        var names = PersonFilterEntries.Where(e => e.IsSelected).Select(e => e.Name).ToList();
        if (names.Count == 0)
        {
            return null;
        }

        var mode = names.Count > 1 ? (PersonFilterMode == GalleryPersonMode.All ? " (UND)" : " (ODER)") : string.Empty;
        return $"{string.Join(", ", names)}{mode}{suffix}";
    }

    /// <summary>Leertext bei aktivem Personen-Filter (null ohne Filter) — <see cref="EmptyMessage"/> fragt ihn ab.</summary>
    public string? EmptyMessagePersons => !HasPersonFilter
        ? HasRelationFilter ? "Keine Fotos mit einer Person dieser Beziehung." : null
        : PersonFilterMode switch
        {
            GalleryPersonMode.NoPerson => "Auf allen Fotos ist mindestens eine Person bestätigt.",
            GalleryPersonMode.UnnamedFaces => "Keine Fotos mit unbenannten Gesichtern — alle erkannten Gesichter sind benannt oder ignoriert.",
            GalleryPersonMode.AnyFace => "Keine Fotos mit Gesichtern — noch nicht analysiert, keine erkannt oder alle ignoriert.",
            _ => "Keine Dateien mit diesem Personen-Filter.",
        };

    partial void OnPersonFilterModeChanged(GalleryPersonMode oldValue, GalleryPersonMode newValue)
    {
        // Nur neu laden, wenn der Filter vorher oder nachher wirkt (ODER ↔ UND ohne Häkchen ändert nichts).
        var anySelected = PersonFilterEntries.Any(e => e.IsSelected);
        static bool Active(GalleryPersonMode mode, bool selected) => mode.IgnoresPersonSelection() || selected;
        if (!_suppressReload && _isInitialized && (Active(oldValue, anySelected) || Active(newValue, anySelected)))
        {
            FireAndForget(ChangeQueryAsync);
        }
    }

    partial void OnPersonFilterIncludeSuggestionsChanged(bool value)
    {
        OnPropertyChanged(nameof(PersonFilterText));
        if (!_suppressReload && _isInitialized
            && ((HasPersonFilter && PersonFilterMode != GalleryPersonMode.AnyFace) || HasRelationFilter)) // Beziehung zählt Vorschläge mit (0.2.47)
        {
            FireAndForget(ChangeQueryAsync);
        }
    }

    /// <summary>Häkchen im Filter-Dropdown geändert → Filter anwenden.</summary>
    [RelayCommand]
    private Task ApplyPersonFilterAsync()
    {
        NotifyPersonFilterChanged();
        return _suppressReload || !_isInitialized ? Task.CompletedTask : ChangeQueryAsync();
    }

    /// <summary>Personen-Teil aufheben (× an „Person …“ in „Es gilt“); der Beziehungs-Teil bleibt (0.2.47).</summary>
    [RelayCommand]
    private Task ClearPersonFilterAsync()
    {
        if (!HasPersonFilter && !PersonFilterIncludeSuggestions)
        {
            return Task.CompletedTask;
        }

        var had = HasPersonFilter;
        ResetPersonPart();
        return had ? ChangeQueryAsync() : Task.CompletedTask;
    }

    /// <summary>Personen- und Beziehungs-Teil zurücksetzen (Bibliothekswechsel, „Filter zurücksetzen“).</summary>
    private void ResetPersonFilter()
    {
        ResetRelationFilter(); // 0.2.47, GalleryViewModel.Relations.cs — zuerst, damit „auch Vorschläge“ mit zurückgeht
        ResetPersonPart();
    }

    private void ResetPersonPart()
    {
        var suppress = _suppressReload;
        _suppressReload = true;
        try
        {
            foreach (var entry in PersonFilterEntries)
            {
                entry.IsSelected = false;
            }

            PersonFilterMode = GalleryPersonMode.Any;
            if (!HasRelationFilter)
            {
                PersonFilterIncludeSuggestions = false; // gilt sonst für die stehen bleibende Beziehung weiter (0.2.47)
            }
        }
        finally
        {
            _suppressReload = suppress;
        }

        NotifyPersonFilterChanged();
    }

    private void NotifyPersonFilterChanged()
    {
        OnPropertyChanged(nameof(HasPersonFilter));
        OnPropertyChanged(nameof(HasPeopleFilter)); // Chip-Hervorhebung (0.2.47)
        OnPropertyChanged(nameof(PersonFilterText));
        OnPropertyChanged(nameof(EmptyMessagePersons));
        OnPropertyChanged(nameof(EmptyMessage));
    }

    /// <summary>Filter für die Abfrage; null ohne Auswahl.</summary>
    private GalleryPersonFilter? SelectedPersonFilter()
    {
        var filter = new GalleryPersonFilter(
            PersonFilterEntries.Where(e => e.IsSelected).Select(e => e.PersonId).ToList(),
            PersonFilterMode,
            PersonFilterIncludeSuggestions,
            SelectedRelationIds()); // Beziehung (0.2.47, GalleryViewModel.Relations.cs)
        return filter.IsActive ? filter : null;
    }

    /// <summary>
    /// Personen und Zähler der aktiven Bibliothek laden; Häkchen bleiben je Person erhalten. Ausgeblendete Personen
    /// (Personen-Seite) erscheinen nur, solange sie gewählt sind.
    /// </summary>
    private async Task LoadPersonsAsync()
    {
        if (!HasLibrary || !_source.IsOpen)
        {
            return;
        }

        try
        {
            var rows = await _source.GetPersonCountsAsync(ScopeLibraryIds, CancellationToken.None); // Bereich (ADR-0048)
            _libraryHasFaces = await _source.HasFacesAsync(ScopeLibraryIds, CancellationToken.None);
            var selected = PersonFilterEntries.Where(e => e.IsSelected).Select(e => e.PersonId).ToHashSet();
            PersonFilterEntries.Clear();
            foreach (var row in rows)
            {
                if (row.IsHidden && !selected.Contains(row.Id))
                {
                    continue;
                }

                PersonFilterEntries.Add(new PersonFilterEntry(row.Id, row.Name, row.FileCount, row.SearchText) { IsSelected = selected.Contains(row.Id) });
            }

            await LoadRelationsAsync(); // Abschnitt „Beziehung“ (0.2.47)

            OnPropertyChanged(nameof(HasPersons));
            NotifyPersonFilterChanged();
            UpdateShowPersonFilter();
            _logger.LogDebug("Personen geladen: {Count}", PersonFilterEntries.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Personen konnten nicht geladen werden");
        }
    }

    /// <summary>Personen der Dateien (Batch) laden und den Items der Page zuordnen.</summary>
    private async Task ApplyPersonsToItemsAsync(IReadOnlyCollection<int> fileIds, CancellationToken ct)
    {
        if (fileIds.Count == 0 || !_source.IsOpen)
        {
            return;
        }

        // Seiten-Laden und Nachrichten laufen unabhängig voneinander: ein älteres Ergebnis darf ein neueres nicht überschreiben.
        var sequence = Interlocked.Increment(ref _personsRequestSequence);
        try
        {
            // Seit 0.2.39 mit der Zahl unbenannter Gesichter (Kachel-Markierung) — weiterhin eine Abfrage je Seite.
            var marks = await _source.GetFaceMarksForFilesAsync(fileIds, ct);
            if (ct.IsCancellationRequested)
            {
                return;
            }

            var byFile = marks.Persons.ToLookup(r => r.FileId);
            await DispatchAsync(() =>
            {
                foreach (var id in fileIds)
                {
                    if (_currentItems.TryGetValue(id, out var item) && item.PersonsSequence < sequence)
                    {
                        item.Persons = GalleryItemPersons.From(byFile[id], marks.UnnamedCount(id));
                        item.PersonsSequence = sequence;
                    }
                }
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Personen der Seite konnten nicht geladen werden");
        }
    }

    private void RegisterPersonMessages()
    {
        _messenger.Register<GalleryViewModel, PersonsChangedMessage>(
            this, static (r, m) => r.RunOnUi(() => r.HandlePersonsChangedAsync(m)));
        _messenger.Register<GalleryViewModel, FacesChangedMessage>(
            this, static (r, m) => r.RunOnUi(() => r.HandleFacesChangedAsync(m)));
    }

    /// <summary>
    /// Personen bestätigt/umbenannt/gelöscht (Personen-Seite, Lightbox, „Gesichtsdaten löschen“): Liste und Zähler neu,
    /// Namen der Page neu; mit aktivem Personen-Filter ändert sich die Trefferliste.
    /// </summary>
    private async Task HandlePersonsChangedAsync(PersonsChangedMessage m)
    {
        if (!HasLibrary || !_source.IsOpen)
        {
            return;
        }

        // Vor dem Neuladen merken: eine gelöschte, gewählte Person verschwindet aus der Liste, der Filter wirkte aber noch.
        var hadFilter = HasPersonFilter || HasRelationFilter;
        await LoadPersonsAsync();
        await ApplyPersonsToItemsAsync(_currentItems.Keys.ToList(), CancellationToken.None);
        if ((hadFilter || HasPersonFilter || HasRelationFilter) && InScope(m.LibraryId))
        {
            ScheduleReload();
        }
    }

    /// <summary>
    /// Gesichter geschrieben (Hintergrund-Index, Abgleich): nur die Namen der betroffenen Dateien dieser Page
    /// nachziehen — kein Neuladen der Trefferliste, sonst würde die Galerie während des Index alle paar Sekunden springen.
    /// </summary>
    private Task HandleFacesChangedAsync(FacesChangedMessage m)
    {
        if (!HasLibrary || !InScope(m.LibraryId))
        {
            return Task.CompletedTask;
        }

        if (!_libraryHasFaces && m.FileIds.Count > 0)
        {
            _libraryHasFaces = true;
            UpdateShowPersonFilter();
        }

        var ids = m.FileIds.Count == 0 ? _currentItems.Keys.ToList() : m.FileIds.Where(_currentItems.ContainsKey).ToList();
        return ApplyPersonsToItemsAsync(ids, CancellationToken.None);
    }
}
