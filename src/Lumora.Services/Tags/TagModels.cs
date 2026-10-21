namespace Lumora.Services.Tags;

/// <summary>Anzeige-DTO eines Tags (ADR-0010: kleine DTOs statt Entities in der UI).</summary>
public sealed record TagInfo(int Id, string Name, string Color, int SortOrder);

/// <summary>Art einer Änderung an der Tag-Liste (nicht an Zuweisungen — dafür <c>FilesChangedMessage</c>).</summary>
public enum TagsChangeKind
{
    Created = 0,
    Renamed = 1,
    Recolored = 2,
    Reordered = 3,
    Deleted = 4,
}

/// <summary>
/// Die Tag-Stammdaten haben sich geändert (angelegt, umbenannt, umgefärbt, umsortiert, gelöscht). Empfänger laden
/// die Tag-Liste neu; Zuweisungen an Dateien werden getrennt als <c>FilesChangedMessage</c> (Kind = Metadata) gemeldet.
/// Transport: WeakReferenceMessenger, ggf. von Worker-Threads.
/// </summary>
public sealed record TagsChangedMessage(TagsChangeKind Kind, int? TagId);

/// <summary>Feste Farbpalette für Tags (Hex „#RRGGBB“); neue Tags bekommen reihum die nächste Farbe.</summary>
public static class TagPalette
{
    public static IReadOnlyList<string> Colors { get; } =
    [
        "#E53935", // Rot
        "#FB8C00", // Orange
        "#FDD835", // Gelb
        "#43A047", // Grün
        "#00897B", // Petrol
        "#1E88E5", // Blau
        "#3949AB", // Indigo
        "#8E24AA", // Violett
        "#D81B60", // Pink
        "#6D4C41", // Braun
        "#546E7A", // Blaugrau
        "#757575", // Grau
    ];

    /// <summary>Farbe für das n-te Tag (0-basiert), reihum durch die Palette.</summary>
    public static string ForIndex(int index) => Colors[Math.Abs(index) % Colors.Count];

    /// <summary>True für „#RRGGBB“ (Groß-/Kleinschreibung egal).</summary>
    public static bool IsValid(string? color)
    {
        if (color is null || color.Length != 7 || color[0] != '#')
        {
            return false;
        }

        for (var i = 1; i < 7; i++)
        {
            if (!Uri.IsHexDigit(color[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Normalisiert auf Großschreibung; wirft bei ungültigem Format.</summary>
    public static string Normalize(string? color)
    {
        if (!IsValid(color))
        {
            throw new ArgumentException($"Ungültige Tag-Farbe „{color}“ — erwartet wird „#RRGGBB“.", nameof(color));
        }

        return color!.ToUpperInvariant();
    }
}
