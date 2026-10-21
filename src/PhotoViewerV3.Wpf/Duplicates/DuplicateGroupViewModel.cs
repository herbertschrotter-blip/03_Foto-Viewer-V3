using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using PhotoViewerV3.Domain.Enums;
using PhotoViewerV3.Persons.Controls;
using PhotoViewerV3.Services.Duplicates;
using PhotoViewerV3.Services.Metadata;
using PhotoViewerV3.Shell;

namespace PhotoViewerV3.Duplicates;

/// <summary>Eine Gruppe der Seite „Doppelte“: Mitglieder nebeneinander, eine davon „behalten“, Gruppe ein-/ausschließbar.</summary>
public sealed partial class DuplicateGroupViewModel : ObservableObject, IAutomationNamed
{
    private readonly Action _selectionChanged;

    public DuplicateGroupViewModel(DuplicateGroup group, bool showLibrary, Action selectionChanged)
    {
        Group = group;
        _selectionChanged = selectionChanged;
        Members = group.Members.Select(m => new DuplicateMemberViewModel(this, m, m.FileId == group.KeeperFileId, showLibrary)).ToList();
    }

    public DuplicateGroup Group { get; }

    public IReadOnlyList<DuplicateMemberViewModel> Members { get; }

    /// <summary>Gruppe wird bei „Übrige in den Papierkorb“ berücksichtigt (Standard an).</summary>
    [ObservableProperty] private bool _isIncluded = true;

    partial void OnIsIncludedChanged(bool value) => _selectionChanged();

    public DuplicateMemberViewModel? Keeper => Members.FirstOrDefault(m => m.IsKeep);

    public IEnumerable<DuplicateMemberViewModel> ToDelete => Members.Where(m => !m.IsKeep);

    /// <summary>„3 Dateien · exakt gleich · 12,4 MB frei“ bzw. „… · ähnlich (Abstand bis 6)“.</summary>
    public string HeaderText
    {
        get
        {
            var kind = Group.Mode == DuplicateMode.Exact
                ? "exakt gleich"
                : string.Create(CultureInfo.CurrentCulture, $"ähnlich (Abstand bis {Group.MaxDistance})");
            return $"{Members.Count} Dateien · {kind} · {Formatting.Bytes(ToDelete.Sum(m => m.Member.Size))} frei";
        }
    }

    /// <summary>Begründung des Vorschlags bzw. Hinweis auf die eigene Wahl.</summary>
    public string ReasonText
        => Keeper is { } keeper && keeper.Member.FileId == Group.KeeperFileId
            ? $"Vorschlag behalten: {Group.KeeperReason}"
            : "Eigene Wahl — der Vorschlag war eine andere Datei.";

    public string AutomationName => $"Gruppe {HeaderText}";

    /// <summary>Von einem Mitglied: es soll die behaltene Datei sein.</summary>
    internal void SetKeep(DuplicateMemberViewModel keep)
    {
        foreach (var member in Members)
        {
            member.SetKeepSilently(ReferenceEquals(member, keep));
        }

        OnPropertyChanged(nameof(Keeper));
        OnPropertyChanged(nameof(HeaderText));
        OnPropertyChanged(nameof(ReasonText));
        _selectionChanged();
    }
}

/// <summary>Eine Datei einer Gruppe (Karte mit Vorschaubild und Angaben).</summary>
public sealed partial class DuplicateMemberViewModel : ObservableObject, IAutomationNamed
{
    private readonly DuplicateGroupViewModel _group;
    private bool _isKeep;

    public DuplicateMemberViewModel(DuplicateGroupViewModel group, DuplicateMember member, bool isKeep, bool showLibrary)
    {
        _group = group;
        Member = member;
        _isKeep = isKeep;
        LocationText = (member.IsInInbox ? "Eingang" : "Bibliothek") + (showLibrary ? $" · {member.LibraryName}" : string.Empty);
    }

    public DuplicateMember Member { get; }

    public int FileId => Member.FileId;

    /// <summary>Vorschaubild (null, bis es geladen ist).</summary>
    [ObservableProperty] private ImageSource? _image;

    /// <summary>Behalten (Radio-Knopf der Gruppe).</summary>
    public bool IsKeep
    {
        get => _isKeep;
        set
        {
            if (value && !_isKeep)
            {
                _group.SetKeep(this);
            }
            else if (!value && _isKeep)
            {
                OnPropertyChanged(); // die behaltene Datei wird nur durch die Wahl einer anderen abgewählt
            }
        }
    }

    internal void SetKeepSilently(bool value)
    {
        if (_isKeep == value)
        {
            return;
        }

        _isKeep = value;
        OnPropertyChanged(nameof(IsKeep));
        OnPropertyChanged(nameof(ActionText));
    }

    public string GroupName => _group.Group.Key;

    public bool IsSuggested => Member.FileId == _group.Group.KeeperFileId;

    public string ActionText => IsKeep ? (IsSuggested ? "Behalten (Vorschlag)" : "Behalten") : "In den Papierkorb";

    public string Name => Member.Name;

    public string FolderText => Member.FolderPath;

    public string FullPath => Member.FullPath;

    public string ResolutionText => KeeperRule.Resolution(Member) is var r && r == "unbekannt" ? "Auflösung unbekannt" : r;

    public string SizeText => Formatting.Bytes(Member.Size);

    /// <summary>„12.07.2024 15:30 (EXIF)“ bzw. „ohne Aufnahmedatum“.</summary>
    public string DateText => Member.CapturedAtUtc is { } utc
        ? CaptureDateText.Format(new CaptureDateValue(
            DateTime.SpecifyKind(utc, DateTimeKind.Utc),
            Member.CapturedAtOffsetMinutes ?? 0,
            Member.CapturedAtSource ?? CaptureDateSource.Unknown,
            CaptureDatePrecision.Exact))
        : "ohne Aufnahmedatum";

    public string LocationText { get; }

    public bool IsInInbox => Member.IsInInbox;

    public string RatingText => Member.Rating is > 0 and var r ? new string('★', r) + new string('☆', 5 - r) : string.Empty;

    public bool IsFavorite => Member.IsFavorite;

    /// <summary>Tags, Kategorien, Personen in Kurzform („2 Tags · 1 Person“), leer ohne.</summary>
    public string ExtrasText
    {
        get
        {
            var parts = new List<string>(3);
            if (Member.TagCount > 0)
            {
                parts.Add(Member.TagCount == 1 ? "1 Tag" : $"{Member.TagCount} Tags");
            }

            if (Member.CategoryCount > 0)
            {
                parts.Add(Member.CategoryCount == 1 ? "1 Kategorie" : $"{Member.CategoryCount} Kategorien");
            }

            if (Member.PersonCount > 0)
            {
                parts.Add(Member.PersonCount == 1 ? "1 Person" : $"{Member.PersonCount} Personen");
            }

            return string.Join(" · ", parts);
        }
    }

    /// <summary>„Abstand 4“ bzw. „byte-gleich“ (nur bei „ähnlich“ und nicht für die behaltene Vorschlags-Datei).</summary>
    public string DistanceText => _group.Group.Mode == DuplicateMode.Similar && !IsSuggested
        ? Member.IsExactCopy ? "byte-gleich" : $"Abstand {Member.Distance}"
        : string.Empty;

    public bool IsVideo => Member.MediaType == MediaType.Video;

    public string AutomationName => $"{Name}, {ActionText}";
}
