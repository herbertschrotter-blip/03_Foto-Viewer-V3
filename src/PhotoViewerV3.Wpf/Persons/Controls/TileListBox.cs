using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace PhotoViewerV3.Persons.Controls;

/// <summary>Kachel mit einem Namen für die UI-Automation (Gesicht, Person, Gruppe).</summary>
public interface IAutomationNamed
{
    string AutomationName { get; }
}

/// <summary>
/// Kachel-Liste der Personen-Seite (Personen, Gruppen, Gesichter) — wie die Galerie (CLAUDE.md): Kacheln sind im
/// UI-Automation-Baum Blätter (<see cref="TileListBoxItem"/>), sonst aktualisiert <c>AutomationPeer.UpdateSubtree</c> bei
/// lauschendem Automation-Client nach jedem Layoutdurchlauf den ganzen Baum. Virtualisiert wird über das
/// <c>VirtualizingTilePanel</c> der Galerie (im XAML als ItemsPanel). Die Auswahl hält das ViewModel der Kachel
/// (<c>IsSelected</c>), nicht die ListBox — <c>SelectionMode</c> bleibt „Single“ nur für den Tastaturfokus.
/// </summary>
public sealed class TileListBox : ListBox
{
    protected override DependencyObject GetContainerForItemOverride() => new TileListBoxItem();

    protected override bool IsItemItsOwnContainerOverride(object item) => item is TileListBoxItem;
}

/// <summary>Container einer Kachel; sein Automation-Peer meldet keine Kinder.</summary>
public sealed class TileListBoxItem : ListBoxItem
{
    protected override AutomationPeer OnCreateAutomationPeer() => new LeafItemPeer(this);

    private sealed class LeafItemPeer : ListBoxItemWrapperAutomationPeer
    {
        public LeafItemPeer(ListBoxItem owner)
            : base(owner)
        {
        }

        protected override List<AutomationPeer>? GetChildrenCore() => null;

        protected override string GetNameCore()
            => (Owner as ListBoxItem)?.DataContext is IAutomationNamed named ? named.AutomationName : base.GetNameCore();
    }
}
