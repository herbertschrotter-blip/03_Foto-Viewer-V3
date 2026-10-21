using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Lumora.Domain.Entities;
using Lumora.Persons.Dialogs;
using Lumora.Services.Faces.Assignment;

namespace Lumora.Persons.ViewModels;

/// <summary>Sortierung der benannten Personen in der Übersicht (0.2.47).</summary>
public enum PersonsSortMode
{
    /// <summary>Nach Anzeigename (Rufname bzw. „Vorname Nachname“).</summary>
    DisplayName,

    /// <summary>Nach Nachname, dann Vorname.</summary>
    LastName,

    /// <summary>Nach Anzahl Fotos, meiste zuerst.</summary>
    PhotoCount,
}

/// <summary>Eintrag der Sortier-Auswahl.</summary>
public sealed record PersonsSortOption(PersonsSortMode Mode, string Label);

/// <summary>Abschnitt der nach Beziehung gruppierten Übersicht („Schwester · 2“).</summary>
public sealed class PersonRelationGroupViewModel(string title, IReadOnlyList<PersonTileViewModel> persons)
{
    public string Title { get; } = title;

    public IReadOnlyList<PersonTileViewModel> Persons { get; } = persons;

    public string Header => $"{Title} · {Persons.Count.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)}";
}

/// <summary>
/// Personen-Details auf der Personen-Seite (0.2.47, ADR-0045): Übersicht durchsuchen (Vor-/Nachname, Rufname,
/// Beziehung), sortieren (Anzeigename / Nachname / Anzahl), nach Beziehung gruppieren; Detailansicht mit allen Feldern
/// und „Bearbeiten…“; Benennen-Dialog mit allen Feldern. Ohne <see cref="IPersonDetailsService"/> (Tests mit
/// Attrappen) wird nur der Anzeigename gespeichert.
/// </summary>
public sealed partial class PersonsViewModel
{
    /// <summary>Text des Benennen-Hinweises, wenn der Anzeigename schon vergeben ist.</summary>
    public const string ExistingPersonHintForGroup = "Es gibt schon eine Person „{0}“ — die Gesichter werden ihr hinzugefügt.";

    private IPersonDetailsService? _details;

    public static IReadOnlyList<PersonsSortOption> SortOptions { get; } =
    [
        new(PersonsSortMode.DisplayName, "Anzeigename"),
        new(PersonsSortMode.LastName, "Nachname"),
        new(PersonsSortMode.PhotoCount, "Anzahl Fotos"),
    ];

    [ObservableProperty] private PersonsSortOption _selectedSort = SortOptions[0];

    /// <summary>Übersicht nach Beziehung gruppieren (Abschnitte, „ohne Beziehung“ am Ende).</summary>
    [ObservableProperty] private bool _groupByRelation;

    /// <summary>Suche in der Übersicht: Anzeigename, Vor-/Nachname, Rufname, Beziehung.</summary>
    [ObservableProperty] private string? _personSearch;

    /// <summary>Abschnitte bei <see cref="GroupByRelation"/>.</summary>
    public ObservableCollection<PersonRelationGroupViewModel> RelationGroups { get; } = new();

    partial void OnSelectedSortChanged(PersonsSortOption value) => ApplyPersons();

    partial void OnGroupByRelationChanged(bool value) => ApplyPersons();

    partial void OnPersonSearchChanged(string? value) => ApplyPersons();

    /// <summary>Gefiltert (ausgeblendete, Suche) und sortiert — Grundlage für die Kacheln.</summary>
    private List<PersonOverview> FilterAndSortPersons()
    {
        var needle = PersonSearch?.Trim();
        var list = _allPersons
            .Where(p => ShowHidden || !p.Person.IsHidden)
            .Where(p => string.IsNullOrEmpty(needle) || p.Person.SearchText.Contains(needle, StringComparison.CurrentCultureIgnoreCase));
        var comparer = StringComparer.CurrentCultureIgnoreCase;
        return (SelectedSort.Mode switch
        {
            PersonsSortMode.LastName => list
                .OrderBy(p => SortLastName(p.Person), comparer)
                .ThenBy(p => p.Person.Details?.FirstName ?? p.Person.Name, comparer),
            PersonsSortMode.PhotoCount => list
                .OrderByDescending(p => p.Person.FileCount)
                .ThenBy(p => p.Person.Name, comparer),
            _ => list.OrderBy(p => p.Person.Name, comparer),
        }).ThenBy(p => p.Person.Id).ToList();
    }

    /// <summary>Ohne Nachnamen zählt der Anzeigename (sonst stünden alle ohne Nachnamen vorne).</summary>
    private static string SortLastName(PersonSummary person)
        => person.Details is { LastName.Length: > 0 } details ? details.LastName : person.Name;

    /// <summary>Abschnitte je Beziehung (nach Name), „Ohne Beziehung“ am Ende; Reihenfolge innerhalb wie sortiert.</summary>
    private void BuildRelationGroups(IReadOnlyList<PersonTileViewModel> tiles)
    {
        RelationGroups.Clear();
        if (!GroupByRelation)
        {
            return;
        }

        var groups = tiles
            .GroupBy(t => t.RelationName ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(g => g.Key.Length == 0 ? 1 : 0)
            .ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase);
        foreach (var group in groups)
        {
            RelationGroups.Add(new PersonRelationGroupViewModel(group.Key.Length == 0 ? "Ohne Beziehung" : group.Key, group.ToList()));
        }

        // Gruppierte Ansicht ist nicht virtualisiert — Titelbilder für alle Personen anfordern (wenige hundert).
        if (_libraryId is { } libraryId && Persons.Count > 0)
        {
            _images.Request(libraryId, Persons.MissingImages(0, Persons.Count - 1));
        }
    }

    // ---- Detailansicht ----------------------------------------------------------------------------------------------

    /// <summary>Zeile unter dem Namen: „Anna Muster · Rufname … · Schwester · geboren 12.03.1950 (74 Jahre) · Das bin ich“.</summary>
    public string? PersonInfoText => CurrentPerson?.Details is { } d ? DescribeDetails(d, DateOnly.FromDateTime(DateTime.Today)) : null;

    /// <summary>Beschreibung der Details (für Tests öffentlich); null, wenn es außer dem Namen nichts zu sagen gibt.</summary>
    public static string? DescribeDetails(PersonDetails details, DateOnly today)
    {
        var parts = new List<string>();
        var fullName = PersonNames.DisplayName(details.FirstName, details.LastName, null);
        if (details.Nickname is not null && fullName.Length > 0)
        {
            parts.Add(fullName); // Rufname ist der Titel — der volle Name gehört dazu
        }

        if (details.RelationName is { } relation)
        {
            parts.Add(relation);
        }

        if (details.BirthDate is { } birth)
        {
            var age = birth.FormatAgeOn(today);
            parts.Add(age is null ? $"geboren {birth.Format()}" : $"geboren {birth.Format()} ({age})");
        }

        if (details.IsMe)
        {
            parts.Add("Das bin ich");
        }

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    /// <summary>„Bearbeiten…“: alle Felder im Dialog; vergebener Anzeigename → Rückfrage „zusammenführen“.</summary>
    [RelayCommand(CanExecute = nameof(CanActOnPerson))]
    private async Task EditPersonAsync()
    {
        if (CurrentPerson is not { } person)
        {
            return;
        }

        var initial = person.Details?.ToInput() ?? PersonDetailsInput.FromName(person.Name);
        var input = await PromptPersonAsync(
            "Person bearbeiten",
            "Vorname, Nachname, Rufname, Beziehung und Geburtsdatum — angezeigt wird der Rufname, sonst Vor- und Nachname.",
            "Speichern",
            initial,
            person.Id,
            "Es gibt schon eine Person „{0}“ — beim Speichern werden beide zusammengeführt.").ConfigureAwait(true);
        if (input is null || input == initial.Normalized())
        {
            return;
        }

        await RunAsync("Speichern", async () =>
        {
            var existing = await _service.FindPersonByNameAsync(input.DisplayName).ConfigureAwait(true);
            if (existing is { } other && other != person.Id)
            {
                if (await _dialogs.ConfirmAsync("Name vergeben", $"Es gibt schon eine Person „{input.DisplayName}“. Beide zusammenführen? Die eingegebenen Angaben gelten danach für sie.", "Zusammenführen").ConfigureAwait(true))
                {
                    await _service.MergePersonsAsync(person.Id, other).ConfigureAwait(true);
                    if (_details is not null)
                    {
                        await _details.UpdatePersonDetailsAsync(other, input).ConfigureAwait(true); // Eingaben nicht verlieren
                    }

                    await LoadPersonAsync(other, keepState: false).ConfigureAwait(true);
                }

                return;
            }

            if (_details is not null)
            {
                await _details.UpdatePersonDetailsAsync(person.Id, input).ConfigureAwait(true);
            }
            else
            {
                await _service.RenamePersonAsync(person.Id, input.DisplayName).ConfigureAwait(true);
            }

            ShowInfo($"„{input.DisplayName}“ gespeichert.");
        }).ConfigureAwait(true);
    }

    /// <summary>
    /// Personen-Dialog mit Vorschlägen (Beziehungen) und Hinweisen (vergebener Name, bisherige „Ich“-Person);
    /// <paramref name="personId"/> = bearbeitete Person (zählt nicht als „vergeben“).
    /// </summary>
    private async Task<PersonDetailsInput?> PromptPersonAsync(
        string title, string message, string confirmText, PersonDetailsInput? initial, int? personId, string existingHint)
    {
        IReadOnlyList<PersonSummary> persons = [];
        IReadOnlyList<string> relations = [];
        try
        {
            persons = await _service.GetPersonsAsync(default).ConfigureAwait(true);
            if (_details is not null)
            {
                relations = (await _details.GetRelationsAsync().ConfigureAwait(true)).Select(r => r.Name).ToList();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Personen-Dialog: Personen bzw. Beziehungen nicht geladen");
        }

        var others = persons.Where(p => p.Id != personId).ToList();
        var me = others.FirstOrDefault(p => p.Details?.IsMe == true)?.Name;
        return await _dialogs.EditPersonAsync(new PersonEditRequest(
            title, message, confirmText, initial, relations, others.Select(p => p.Name).ToList(), existingHint, me)).ConfigureAwait(true);
    }

    /// <summary>Neue Person mit allen Details (bzw. nur Anzeigename ohne Details-Dienst).</summary>
    private Task<int> CreatePersonAsync(PersonDetailsInput input)
        => _details is not null ? _details.CreatePersonAsync(input) : _service.CreatePersonAsync(input.DisplayName);
}
