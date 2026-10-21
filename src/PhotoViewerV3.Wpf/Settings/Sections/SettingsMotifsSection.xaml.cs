using System.Windows.Controls;

namespace PhotoViewerV3.Settings.Sections;

/// <summary>
/// Einstellungen › Motive (ADR-0052): Erkennung an/aus, Grafikkarte, Modell-Download, Index, Schwelle und Begriffe der
/// aktiven Bibliothek. DataContext = <see cref="Motifs.MotifSettingsViewModel"/> (von der SettingsPage gesetzt); lädt
/// beim Anzeigen neu.
/// </summary>
public partial class SettingsMotifsSection : UserControl
{
    public SettingsMotifsSection()
    {
        InitializeComponent();
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true && DataContext is Motifs.MotifSettingsViewModel vm)
            {
                vm.Load();
            }
        };
    }
}
