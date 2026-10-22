using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Lumora.Stories.Assistant;

/// <summary>Knopf, der beim Klick sein Kontextmenü unter sich öffnet (änderbare Punkte der Karte „So habe ich es verstanden“).</summary>
public sealed class AssistantMenuButton : Button
{
    protected override void OnClick()
    {
        base.OnClick();
        if (ContextMenu is { } menu && menu.Items.Count > 0)
        {
            menu.PlacementTarget = this;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }
}
