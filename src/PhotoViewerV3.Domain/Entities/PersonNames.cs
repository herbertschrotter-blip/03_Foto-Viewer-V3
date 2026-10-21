namespace PhotoViewerV3.Domain.Entities;

/// <summary>
/// Namensregeln der Personen (ADR-0045): Anzeigename = Rufname, sonst „Vorname Nachname“; alte Einzelnamen werden am
/// ERSTEN Leerzeichen geteilt (erstes Wort = Vorname, Rest = Nachname, ein Wort = nur Vorname). Die Teilung ist
/// verlustfrei: <c>Vorname + " " + Nachname</c> ergibt den alten (getrimmten) Namen wieder.
/// </summary>
public static class PersonNames
{
    /// <summary>Anzeigename; leer, wenn alle Teile leer sind.</summary>
    public static string DisplayName(string? firstName, string? lastName, string? nickname)
    {
        var nick = Clean(nickname);
        if (nick.Length > 0)
        {
            return nick;
        }

        var first = Clean(firstName);
        var last = Clean(lastName);
        return first.Length == 0 ? last : last.Length == 0 ? first : first + " " + last;
    }

    /// <summary>Teilt einen (alten) Einzelnamen am ersten Leerzeichen — wie die Migration AddPersonDetails.</summary>
    public static (string FirstName, string LastName) Split(string? name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        var space = trimmed.IndexOf(' ');
        return space < 0 ? (trimmed, string.Empty) : (trimmed[..space], trimmed[(space + 1)..]);
    }

    /// <summary>Getrimmt; null wird leer.</summary>
    public static string Clean(string? value) => (value ?? string.Empty).Trim();

    /// <summary>Getrimmt; leer wird null (für optionale Felder wie den Rufnamen).</summary>
    public static string? CleanOrNull(string? value)
    {
        var cleaned = Clean(value);
        return cleaned.Length == 0 ? null : cleaned;
    }
}
