using System.Collections.Generic;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;

namespace Lumora.Stories;

/// <summary>
/// Knopf für Karten und Foto-Kacheln der Story-Seite, der im UI-Automation-Baum ein Blatt ist (Name aus
/// <c>AutomationProperties.Name</c>, Innenleben unsichtbar) — wie die Galerie-Kacheln (CLAUDE.md: sonst blockiert
/// <c>AutomationPeer.UpdateSubtree</c> den UI-Thread, sobald ein Automation-Client lauscht). Doppelklick löst
/// <see cref="DoubleClickCommand"/> aus (Lightbox), ein einfacher Klick den normalen <c>Command</c>.
/// </summary>
public sealed class StoryLeafButton : Button
{
    public static readonly System.Windows.DependencyProperty DoubleClickCommandProperty = System.Windows.DependencyProperty.Register(
        nameof(DoubleClickCommand), typeof(ICommand), typeof(StoryLeafButton));

    public ICommand? DoubleClickCommand
    {
        get => (ICommand?)GetValue(DoubleClickCommandProperty);
        set => SetValue(DoubleClickCommandProperty, value);
    }

    protected override void OnMouseDoubleClick(MouseButtonEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.ChangedButton == MouseButton.Left && DoubleClickCommand is { } command && command.CanExecute(CommandParameter))
        {
            e.Handled = true;
            command.Execute(CommandParameter);
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new LeafPeer(this);

    private sealed class LeafPeer(Button owner) : ButtonAutomationPeer(owner)
    {
        protected override List<AutomationPeer>? GetChildrenCore() => null;
    }
}
