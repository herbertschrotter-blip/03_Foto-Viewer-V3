using System;
using System.Collections.Generic;

namespace PhotoViewerV3.Shell;

/// <summary>
/// Tags der Navigationsziele. Seiten der linken Leiste (NavigationViewItem.TargetPageTag in MainWindow.xaml): Galerie,
/// Ordner, Personen, Orte, Sortieren, Einstellungen. Die übrigen Tags sind Reiter der Einstellungen (ADR-0042) — der
/// <see cref="INavigationService"/> öffnet dafür die Einstellungen und wählt den Reiter; <see cref="INavigationService.CurrentTag"/>
/// ist dann <see cref="Settings"/>, der Reiter steht in <see cref="SettingsSectionState.SelectedSection"/>.
/// </summary>
public static class NavigationTags
{
    public const string Gallery = "gallery";
    public const string Folders = "folders";
    public const string Persons = "persons"; // Seite „Personen“ (ADR-0037)
    public const string Places = "places"; // Seite „Orte“ (ADR-0051)
    public const string Duplicates = "duplicates"; // Seite „Doppelte“ (ADR-0053)
    public const string Albums = "albums"; // Seite „Alben“ (ADR-0054)
    public const string Sorter = "sorter";
    public const string Settings = "settings";

    // ---- Reiter der Einstellungen (ADR-0042), in Anzeigereihenfolge ----

    /// <summary>Einstellungen › Bibliotheken &amp; Eingänge (bis 0.2.43 eigene Seite „Bibliothek“).</summary>
    public const string Library = "library";

    /// <summary>Einstellungen › Tags (bis 0.2.43 eigene Seite).</summary>
    public const string Tags = "tags";

    /// <summary>Einstellungen › Kategorien (bis 0.2.43 eigene Seite).</summary>
    public const string Categories = "categories";

    public const string SettingsFaces = "settings-faces";

    /// <summary>Einstellungen › Motive (ADR-0052).</summary>
    public const string SettingsMotifs = "settings-motifs";
    public const string SettingsArchives = "settings-archives";
    public const string SettingsAppearance = "settings-appearance";
    public const string SettingsStorage = "settings-storage";
    public const string SettingsInfo = "settings-info";

    /// <summary>Alle Reiter der Einstellungen in Anzeigereihenfolge.</summary>
    public static IReadOnlyList<string> SettingsSections { get; } =
    [
        Library, Tags, Categories, SettingsFaces, SettingsMotifs, SettingsArchives, SettingsAppearance, SettingsStorage, SettingsInfo,
    ];

    /// <summary>
    /// Einstellungen › Versteckte Bibliotheken (ADR-0049) — nur mit Startschalter <c>_hidden</c> vorhanden, deshalb nicht in
    /// <see cref="SettingsSections"/>.
    /// </summary>
    public const string SettingsHiddenLibraries = "settings-hidden";

    /// <summary>true, wenn <paramref name="tag"/> ein Reiter der Einstellungen ist (keine eigene Seite).</summary>
    public static bool IsSettingsSection(string? tag)
    {
        if (string.Equals(tag, SettingsHiddenLibraries, StringComparison.Ordinal))
        {
            return true;
        }

        foreach (var section in SettingsSections)
        {
            if (string.Equals(section, tag, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Programmatische Navigation zwischen den Seiten der Shell. Singleton; von jedem Thread aufrufbar
/// (wird auf den UI-Thread gestellt). Aufrufe vor dem Anbinden des Hauptfensters werden gemerkt.
/// </summary>
public interface INavigationService
{
    /// <summary>Tag der aktuell angezeigten Seite oder null, solange noch nicht navigiert wurde (nie ein Reiter-Tag).</summary>
    string? CurrentTag { get; }

    /// <summary>Wird nach jeder Navigation (Klick oder NavigateTo) mit dem Ziel-Tag ausgelöst (UI-Thread).</summary>
    event EventHandler<string>? Navigated;

    /// <summary>
    /// Navigiert zur Seite mit dem Tag (siehe <see cref="NavigationTags"/>); ein Reiter-Tag der Einstellungen öffnet die
    /// Einstellungen auf diesem Reiter.
    /// </summary>
    void NavigateTo(string tag);
}
