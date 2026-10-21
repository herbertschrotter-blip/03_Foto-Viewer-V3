using System;
using System.Collections.Generic;
using System.Linq;
using PhotoViewerV3.Services.Library;

namespace PhotoViewerV3.Libraries;

/// <summary>Zeile der Bibliotheksliste (unveränderlich; die Liste wird bei jedem Laden neu aufgebaut).</summary>
public sealed class LibraryListItem
{
    public LibraryListItem(LibraryInfo info, bool isActive)
    {
        Id = info.Id;
        Name = info.Name;
        RootPath = info.RootPath;
        FolderCount = info.FolderCount;
        FileCount = info.FileCount;
        IsRootAvailable = info.IsRootAvailable;
        CreatedAt = info.CreatedAt;
        LastOpenedAt = info.LastOpenedAt;
        IsActive = isActive;
        Roots = info.Roots.Select(r => new LibraryRootListItem(this, r)).ToList();
    }

    public int Id { get; }

    public string Name { get; }

    /// <summary>Pfad der Archiv-Wurzel (Anzeige-/Kompatibilitätsfeld, ADR-0020).</summary>
    public string RootPath { get; }

    public int FolderCount { get; }

    public int FileCount { get; }

    /// <summary>Archiv-Wurzel erreichbar.</summary>
    public bool IsRootAvailable { get; }

    public DateTime CreatedAt { get; }

    public DateTime? LastOpenedAt { get; }

    public bool IsActive { get; }

    /// <summary>Alle Wurzeln (Archiv zuerst, dann Eingänge) mit Rolle und Erreichbarkeit.</summary>
    public IReadOnlyList<LibraryRootListItem> Roots { get; }

    public int InboxCount => Roots.Count(r => r.IsInbox);

    /// <summary>true, wenn mindestens eine Wurzel nicht erreichbar ist (Hinweis-Badge).</summary>
    public bool HasOfflineRoot => Roots.Any(r => !r.IsAvailable);

    /// <summary>Noch keine Scan-Daten → Fresh-Scan; sonst „Aktualisieren“ (Incremental) als Primär- und „Neu aufbauen“ (ClearAndRescan) als Sekundäraktion.</summary>
    public bool IsEmpty => FolderCount == 0 && FileCount == 0;

    /// <summary>„Neu aufbauen“ nur anbieten, wenn es etwas neu aufzubauen gibt.</summary>
    public bool CanRebuild => !IsEmpty;

    public string CountsText => IsEmpty
        ? "Noch nicht gescannt"
        : $"{FolderCount:N0} Ordner · {FileCount:N0} Dateien";

    public string RootsText => InboxCount switch
    {
        0 => "Bibliotheksordner ohne Eingang",
        1 => "Bibliotheksordner + 1 Eingang",
        _ => $"Bibliotheksordner + {InboxCount} Eingänge",
    };

    public string TimestampText => LastOpenedAt is { } opened
        ? $"zuletzt geöffnet {opened.ToLocalTime():g}"
        : $"angelegt {CreatedAt.ToLocalTime():g}";

    // ---- Verknüpfung (ADR-0048) — vom ViewModel vor dem Anzeigen gesetzt (Liste wird bei jedem Laden neu gebaut) ----

    /// <summary>Verknüpfungsgruppe (0 = unbekannt).</summary>
    public int GroupId { get; private set; }

    /// <summary>Anzeigename der Gruppe („Alle verknüpften (Name)“).</summary>
    public string GroupName { get; private set; } = string.Empty;

    /// <summary>Namen der anderen Bibliotheken derselben Gruppe.</summary>
    public IReadOnlyList<string> LinkedLibraryNames { get; private set; } = [];

    public bool IsLinked => LinkedLibraryNames.Count > 0;

    /// <summary>Verknüpfen ist in diesem Katalog erlaubt (nicht in einer versteckten Bibliothek).</summary>
    public bool ShowLinking { get; private set; }

    /// <summary>„Verknüpfen mit…“ anbieten: erlaubt und es gibt eine Bibliothek außerhalb der eigenen Gruppe.</summary>
    public bool CanLink { get; private set; }

    public string LinkText => IsLinked
        ? $"Verknüpft mit {string.Join(", ", LinkedLibraryNames.Select(n => $"„{n}“"))} — Gruppe „{GroupName}“: Tags, Kategorien, Personen und Beziehungen gemeinsam"
        : "Nicht verknüpft — Tags, Kategorien, Personen und Beziehungen gelten nur in dieser Bibliothek";

    internal void ApplyGroup(LibraryGroupInfo? group, IReadOnlyDictionary<int, string> namesById, bool allowsLinking, bool hasLibraryOutsideGroup)
    {
        GroupId = group?.Id ?? 0;
        GroupName = group?.Name ?? Name;
        LinkedLibraryNames = group is null
            ? []
            : group.LibraryIds.Where(id => id != Id).Select(id => namesById.GetValueOrDefault(id, $"#{id}")).ToList();
        ShowLinking = allowsLinking;
        CanLink = allowsLinking && hasLibraryOutsideGroup;
    }

    public string ScanButtonText => IsEmpty ? "Scannen" : "Aktualisieren";

    public string ScanButtonToolTip => IsEmpty
        ? "Alle Wurzeln durchsuchen und Katalog aufbauen"
        : "Änderungen übernehmen (alle Wurzeln): neue Dateien aufnehmen, fehlende markieren, geänderte aktualisieren — Favoriten und Bewertungen bleiben erhalten";
}

/// <summary>Wurzelordner einer Bibliothek in der Bibliotheksliste (ADR-0020).</summary>
public sealed class LibraryRootListItem
{
    public LibraryRootListItem(LibraryListItem library, LibraryRootInfo info)
    {
        Library = library;
        Id = info.Id;
        LibraryId = info.LibraryId;
        Path = info.Path;
        Name = info.Name;
        IsArchive = info.IsArchive;
        IsAvailable = info.IsAvailable;
        RoleText = info.RoleText;
    }

    public LibraryListItem Library { get; }

    public int Id { get; }

    public int LibraryId { get; }

    public string Path { get; }

    public string Name { get; }

    public string RoleText { get; }

    public bool IsArchive { get; }

    public bool IsInbox => !IsArchive;

    public bool IsAvailable { get; }

    /// <summary>Nur Eingangs-Wurzeln lassen sich entfernen oder zum Archiv machen; die Archiv-Wurzel bleibt (genau eine je Bibliothek).</summary>
    public bool CanRemove => IsInbox;

    public bool CanMakeArchive => IsInbox;
}
