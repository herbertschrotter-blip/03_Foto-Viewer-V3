using System.Windows.Controls;
using Lumora.Shell.HiddenLibraries;
using Wpf.Ui.Controls;
using MenuItem = System.Windows.Controls.MenuItem;

namespace Lumora.Shell;

/// <summary>
/// Einträge der versteckten Bibliotheken im Umschalter-Menü (ADR-0049) — nur mit Startschalter <c>_hidden</c>:
/// in der normalen Sitzung die gefundenen versteckten Bibliotheken, „Versteckte Bibliothek anlegen…“ und
/// „Container öffnen…“; in einer versteckten Sitzung „Zusperren“.
/// </summary>
public partial class LibrarySwitcher
{
    private static void AddHiddenLibraryItems(ContextMenu menu, HiddenLibrariesViewModel? hidden)
    {
        if (hidden is not { IsHiddenModeEnabled: true })
        {
            return;
        }

        menu.Items.Add(new Separator());
        if (hidden.IsHiddenSessionActive)
        {
            menu.Items.Add(new MenuItem
            {
                Header = "Zusperren (zurück zur normalen Bibliothek)",
                Icon = new SymbolIcon { Symbol = SymbolRegular.LockClosed24 },
                Command = hidden.LockCommand,
            });
            return;
        }

        menu.Items.Add(new MenuItem { Header = "Versteckte Bibliotheken", IsEnabled = false });
        foreach (var library in hidden.Found)
        {
            menu.Items.Add(new MenuItem
            {
                Header = library.Name,
                InputGestureText = library.DriveRoot,
                Icon = new SymbolIcon { Symbol = SymbolRegular.LockClosed24 },
                Command = hidden.OpenCommand,
                CommandParameter = library,
            });
        }

        menu.Items.Add(new MenuItem
        {
            Header = "Versteckte Bibliothek anlegen…",
            Icon = new SymbolIcon { Symbol = SymbolRegular.Add24 },
            Command = hidden.CreateCommand,
        });
        menu.Items.Add(new MenuItem
        {
            Header = "Container öffnen…",
            Icon = new SymbolIcon { Symbol = SymbolRegular.LockOpen24 },
            Command = hidden.MountContainerCommand,
        });
    }
}
