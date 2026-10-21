using System;
using System.Collections.Generic;

namespace Lumora.Gallery.ViewModels;

/// <summary>
/// Kachel-Markierung für unbenannte Gesichter (0.2.39): neben dem Personen-Symbol (bestätigte Personen) zeigt die Kachel
/// ein graues Gesichts-Symbol mit der Zahl der noch unbenannten Gesichter (unbestätigt oder Vorschlag). Ignorierte
/// Gesichter zählen nicht — sind alle Gesichter eines Fotos ignoriert, erscheint kein Symbol.
/// </summary>
public sealed partial class GalleryItemPersons
{
    /// <summary>Unbenannte Gesichter (Zustand 0/1, dazu bestätigt ohne Person) — wie der Filter „unbenannte Gesichter“.</summary>
    public int UnnamedCount { get; }

    /// <summary>Kachel zeigt das graue Gesichts-Symbol.</summary>
    public bool HasUnnamed => UnnamedCount > 0;

    /// <summary>Tooltip des Gesichts-Symbols: „2 Gesichter noch nicht benannt“, mit Vorschlägen in einer zweiten Zeile.</summary>
    public string UnnamedToolTip
    {
        get
        {
            if (!HasUnnamed)
            {
                return string.Empty;
            }

            var text = UnnamedCount == 1 ? "1 Gesicht noch nicht benannt" : $"{UnnamedCount} Gesichter noch nicht benannt";
            return HasSuggestions ? text + Environment.NewLine + "Vorschläge: " + SuggestionsText : text;
        }
    }

    /// <summary>
    /// Kurztext für den Automation-Peer der Kachel (HelpText; die Kachel bleibt ein Blatt): „Personen: Anna; 2 Gesichter
    /// noch nicht benannt“ — leer ohne Personen und unbenannte Gesichter.
    /// </summary>
    public string AutomationText
    {
        get
        {
            var parts = new List<string>(2);
            if (HasConfirmed)
            {
                parts.Add("Personen: " + ConfirmedText);
            }

            if (HasUnnamed)
            {
                parts.Add(UnnamedCount == 1 ? "1 Gesicht noch nicht benannt" : $"{UnnamedCount} Gesichter noch nicht benannt");
            }

            return string.Join("; ", parts);
        }
    }
}
