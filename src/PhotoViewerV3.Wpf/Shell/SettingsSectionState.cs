using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PhotoViewerV3.Shell;

/// <summary>
/// Gewählter Reiter der Einstellungen (ADR-0042) als gemeinsamer Zustand: der <see cref="NavigationService"/> setzt ihn
/// bei der Navigation auf einen Reiter-Tag, die Einstellungsseite zeigt ihn an und schreibt Klicks auf die Reiter zurück.
/// Singleton; nur auf dem UI-Thread ändern.
/// </summary>
public sealed partial class SettingsSectionState : ObservableObject
{
    /// <summary>Einer der Tags aus <see cref="NavigationTags.SettingsSections"/>; Standard „Bibliotheken &amp; Eingänge“.</summary>
    [ObservableProperty]
    private string _selectedSection = NavigationTags.Library;

    partial void OnSelectedSectionChanging(string value)
    {
        if (!NavigationTags.IsSettingsSection(value))
        {
            throw new ArgumentException($"Unbekannter Reiter der Einstellungen: {value}", nameof(value));
        }
    }
}
