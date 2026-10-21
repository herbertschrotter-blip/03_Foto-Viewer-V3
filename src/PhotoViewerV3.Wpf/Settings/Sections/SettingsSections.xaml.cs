using System.Windows.Controls;

namespace PhotoViewerV3.Settings.Sections;

// Reiter-Inhalte der Einstellungen (ADR-0042). Reine Ansichten ohne Code: DataContext = SettingsViewModel (geerbt von der
// SettingsPage bzw. für die Ordnerüberwachung von der SettingsPage gesetzt). Inhalte bis 0.2.43 in SettingsPage.xaml.

/// <summary>Einstellungen › Darstellung (Farbschema).</summary>
public partial class SettingsAppearanceSection : UserControl
{
    public SettingsAppearanceSection() => InitializeComponent();
}

/// <summary>Einstellungen › Speicher &amp; Wartung (Speicherorte, Katalog umziehen, Vorschaubilder, FFmpeg, Formate).</summary>
public partial class SettingsStorageSection : UserControl
{
    public SettingsStorageSection() => InitializeComponent();
}

/// <summary>Einstellungen › Archive (automatisch entpacken je Bibliothek, vorhandene nachholen).</summary>
public partial class SettingsArchivesSection : UserControl
{
    public SettingsArchivesSection() => InitializeComponent();
}

/// <summary>Einstellungen › Gesichter (Gesichtserkennung, Mindestgröße, Modell, Index, Gesichtsdaten löschen).</summary>
public partial class SettingsFacesSection : UserControl
{
    public SettingsFacesSection() => InitializeComponent();
}

/// <summary>Einstellungen › Gesichter › Karte „Personen &amp; Beziehungen“ (0.2.47, ADR-0045); DataContext = PersonRelationsSettingsViewModel.</summary>
public partial class SettingsRelationsSection : UserControl
{
    public SettingsRelationsSection() => InitializeComponent();
}

/// <summary>Ordnerüberwachung als Karte unter der Bibliotheksliste in Einstellungen › Bibliotheken &amp; Eingänge.</summary>
public partial class SettingsWatchSection : UserControl
{
    public SettingsWatchSection() => InitializeComponent();
}
