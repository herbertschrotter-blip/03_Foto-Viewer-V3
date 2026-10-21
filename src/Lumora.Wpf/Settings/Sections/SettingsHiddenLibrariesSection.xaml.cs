using System.Windows.Controls;

namespace Lumora.Settings.Sections;

/// <summary>
/// Einstellungen › Versteckte Bibliotheken (ADR-0049); nur mit Startschalter <c>_hidden</c> eingefügt
/// (<see cref="Shell.HiddenLibraries.HiddenLibrariesUi.AddSettingsTab"/>). DataContext = HiddenLibrariesViewModel.
/// </summary>
public partial class SettingsHiddenLibrariesSection : UserControl
{
    public SettingsHiddenLibrariesSection() => InitializeComponent();
}
