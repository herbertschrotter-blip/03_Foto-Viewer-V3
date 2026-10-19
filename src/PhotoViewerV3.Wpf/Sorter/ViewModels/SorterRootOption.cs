using PhotoViewerV3.Domain.Enums;
using PhotoViewerV3.Services.Library;
using PhotoViewerV3.Services.Sorter;

namespace PhotoViewerV3.Sorter.ViewModels;

/// <summary>
/// Eintrag der Wurzel-Auswahl (ComboBox) für Quelle und Ziel: eine Wurzel der Bibliothek mit Rolle, Name, Pfad und
/// Erreichbarkeit — oder beim Ziel der Sondereintrag <see cref="SourceFolderOption"/> („in den Quellordner“).
/// </summary>
public sealed class SorterRootOption
{
    private SorterRootOption(int? rootId, string? path, LibraryRootRole? role, string name, bool isAvailable)
    {
        RootId = rootId;
        Path = path;
        Role = role;
        Name = name;
        IsAvailable = isAvailable;
    }

    public SorterRootOption(LibraryRootInfo root)
        : this(root.Id, SorterPaths.NormalizeDirectory(root.Path), root.Role, root.Name, root.IsAvailable)
    {
    }

    /// <summary>Ziel = Quellordner (die Gruppen-/Ebenenordner entstehen im Quellordner selbst).</summary>
    public static SorterRootOption SourceFolderOption { get; } = new(null, null, null, "Quellordner", isAvailable: true);

    /// <summary>Id der Wurzel; null beim Sondereintrag „Quellordner“.</summary>
    public int? RootId { get; }

    /// <summary>Normalisierter Pfad der Wurzel; null beim Sondereintrag.</summary>
    public string? Path { get; }

    public LibraryRootRole? Role { get; }

    public string Name { get; }

    public bool IsAvailable { get; }

    public bool IsSourceFolderOption => RootId is null;

    public bool IsArchive => Role == LibraryRootRole.Archive;

    public bool IsInbox => Role == LibraryRootRole.Inbox;

    /// <summary>„Archiv“ / „Eingang“ bzw. „Ziel“ beim Sondereintrag.</summary>
    public string RoleText => Role is { } role ? LibraryRootInfo.RoleLabel(role) : "Ziel";

    /// <summary>Zweite Zeile im Eintrag: Pfad (mit „offline“) bzw. Erklärung des Sondereintrags.</summary>
    public string Detail => IsSourceFolderOption
        ? "die Ordner entstehen im Quellordner selbst"
        : IsAvailable ? Path! : Path + " — offline";

    public override string ToString() => IsSourceFolderOption ? Name : $"{RoleText} · {Name} · {Path}";
}
