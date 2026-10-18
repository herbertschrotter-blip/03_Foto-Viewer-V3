using System;
using PhotoViewerV3.Services.Library;

namespace PhotoViewerV3.Library;

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
    }

    public int Id { get; }

    public string Name { get; }

    public string RootPath { get; }

    public int FolderCount { get; }

    public int FileCount { get; }

    public bool IsRootAvailable { get; }

    public DateTime CreatedAt { get; }

    public DateTime? LastOpenedAt { get; }

    public bool IsActive { get; }

    /// <summary>Noch keine Scan-Daten → Fresh-Scan, sonst ClearAndRescan.</summary>
    public bool IsEmpty => FolderCount == 0 && FileCount == 0;

    public string CountsText => IsEmpty
        ? "Noch nicht gescannt"
        : $"{FolderCount:N0} Ordner · {FileCount:N0} Dateien";

    public string TimestampText => LastOpenedAt is { } opened
        ? $"zuletzt geöffnet {opened.ToLocalTime():g}"
        : $"angelegt {CreatedAt.ToLocalTime():g}";

    public string ScanButtonText => IsEmpty ? "Scannen" : "Neu scannen";
}
