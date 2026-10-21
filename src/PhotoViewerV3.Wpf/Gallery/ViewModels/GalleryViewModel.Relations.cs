using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PhotoViewerV3.Gallery.People;

namespace PhotoViewerV3.Gallery.ViewModels;

/// <summary>
/// Filter nach Beziehung im Personen-Flyout (0.2.47, ADR-0045): Abschnitt „Beziehung“ mit Häkchen je Beziehung (ODER
/// untereinander), UND-verknüpft mit dem Personen-Teil und allen anderen Filtern; eigene Bedingung in der Zeile „Es gilt“
/// („Beziehung „Schwester““). Die Abfrage nimmt die Ids über <see cref="Data.GalleryPersonFilter.RelationIds"/> mit.
/// </summary>
public sealed partial class GalleryViewModel
{
    /// <summary>Beziehungen im Flyout (nur solche, die mindestens eine Person trägt).</summary>
    public ObservableCollection<RelationFilterEntry> RelationFilterEntries { get; } = new();

    public bool HasRelations => RelationFilterEntries.Count > 0;

    public bool HasRelationFilter => RelationFilterEntries.Any(e => e.IsSelected);

    /// <summary>Chip „Personen“ hervorheben: Personen- oder Beziehungs-Teil wirkt.</summary>
    public bool HasPeopleFilter => HasPersonFilter || HasRelationFilter;

    /// <summary>„Beziehung „Schwester““ bzw. „Beziehung „Schwester“ oder „Bruder““; null ohne Auswahl.</summary>
    public string? RelationConditionLabel
    {
        get
        {
            var names = RelationFilterEntries.Where(e => e.IsSelected).Select(e => $"„{e.Name}“").ToList();
            // Der Personen-Teil zeigt „(mit Vorschlägen)“ schon — außer „irgendein Gesicht“, das Vorschläge ohnehin zählt.
            var suffix = PersonFilterIncludeSuggestions && (!HasPersonFilter || PersonFilterMode == Data.GalleryPersonMode.AnyFace)
                ? " (mit Vorschlägen)"
                : string.Empty;
            return names.Count == 0 ? null : $"Beziehung {string.Join(" oder ", names)}{suffix}";
        }
    }

    private IReadOnlyList<int> SelectedRelationIds() => RelationFilterEntries.Where(e => e.IsSelected).Select(e => e.RelationId).ToList();

    /// <summary>Häkchen einer Beziehung geändert → Filter anwenden.</summary>
    [RelayCommand]
    private Task ApplyRelationFilterAsync()
    {
        NotifyRelationFilterChanged();
        return _suppressReload || !_isInitialized ? Task.CompletedTask : ChangeQueryAsync();
    }

    /// <summary>× an der Bedingung „Beziehung …“.</summary>
    [RelayCommand]
    private Task ClearRelationFilterAsync()
    {
        if (!HasRelationFilter)
        {
            return Task.CompletedTask;
        }

        ResetRelationFilter();
        return _isInitialized ? ChangeQueryAsync() : Task.CompletedTask;
    }

    /// <summary>„Leeren“ im Flyout: Personen- und Beziehungs-Teil.</summary>
    [RelayCommand]
    private Task ClearPeopleFilterAsync()
    {
        var had = HasPeopleFilter;
        var suppress = _suppressReload;
        _suppressReload = true;
        try
        {
            ResetRelationFilter();
            ResetPersonPart();
        }
        finally
        {
            _suppressReload = suppress;
        }

        return had && _isInitialized ? ChangeQueryAsync() : Task.CompletedTask;
    }

    private void ResetRelationFilter()
    {
        foreach (var entry in RelationFilterEntries)
        {
            entry.IsSelected = false;
        }

        NotifyRelationFilterChanged();
    }

    private void NotifyRelationFilterChanged()
    {
        OnPropertyChanged(nameof(HasRelationFilter));
        OnPropertyChanged(nameof(HasPeopleFilter));
        OnPropertyChanged(nameof(RelationConditionLabel));
        OnPropertyChanged(nameof(EmptyMessagePersons));
        OnPropertyChanged(nameof(EmptyMessage));
        OnPropertyChanged(nameof(PersonFilterText)); // Chip-Wert und „Es gilt“ (OnFilterTextChanged)
    }

    /// <summary>Beziehungen mit Zählern laden; Häkchen bleiben je Beziehung erhalten (aus LoadPersonsAsync).</summary>
    private async Task LoadRelationsAsync()
    {
        try
        {
            var rows = await _source.GetRelationCountsAsync(_libraryId, CancellationToken.None);
            var selected = SelectedRelationIds().ToHashSet();
            var hadFilter = selected.Count > 0;
            RelationFilterEntries.Clear();
            foreach (var row in rows)
            {
                RelationFilterEntries.Add(new RelationFilterEntry(row.Id, row.Name, row.FileCount) { IsSelected = selected.Contains(row.Id) });
            }

            OnPropertyChanged(nameof(HasRelations));
            NotifyRelationFilterChanged();
            if (hadFilter && !HasRelationFilter && !_suppressReload && _isInitialized)
            {
                ScheduleReload(); // gewählte Beziehung gelöscht/zusammengeführt → Filter wirkt nicht mehr
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Beziehungen konnten nicht geladen werden");
        }
    }
}
