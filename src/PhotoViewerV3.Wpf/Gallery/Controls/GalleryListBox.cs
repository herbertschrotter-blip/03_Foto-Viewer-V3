using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace PhotoViewerV3.Gallery.Controls;

/// <summary>
/// ListBox für das Kachel-Grid, deren Kacheln im UI-Automation-Baum Blätter sind.
/// Hintergrund (v0.2.0-Hänger): Sobald ein UI-Automation-Client lauscht (Visual Studio Live Visual Tree,
/// Bildschirmleser, Automationsskripte), aktualisiert WPF nach jedem Layoutdurchlauf den gesamten Peer-Baum
/// (<c>AutomationPeer.UpdateSubtree</c>). Bei 500 nicht virtualisierten Kacheln mit je rund zehn Elementen und
/// einem Layoutdurchlauf je eintreffendem Vorschaubild blockierte das den UI-Thread dauerhaft.
/// Mit Blatt-Peers bleibt die Kachel als Listeneintrag sichtbar (Name = Dateiname), ihr Innenleben nicht.
/// </summary>
public sealed class GalleryListBox : ListBox
{
    protected override DependencyObject GetContainerForItemOverride() => new GalleryListBoxItem();

    protected override bool IsItemItsOwnContainerOverride(object item) => item is GalleryListBoxItem;
}

/// <summary>Container einer Kachel; sein Automation-Peer meldet keine Kinder (siehe <see cref="GalleryListBox"/>).</summary>
public sealed class GalleryListBoxItem : ListBoxItem
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
        {
            var name = base.GetNameCore();
            if (!string.IsNullOrEmpty(name))
            {
                return name;
            }

            return (Owner as ListBoxItem)?.DataContext is ViewModels.GalleryItem item ? item.Name : string.Empty;
        }
    }
}
