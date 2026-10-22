using System.Windows.Controls.Primitives;

namespace Lumora.Stories;

/// <summary>
/// Segment- bzw. Chip-Knopf der Story-Seite (Art-Filter, Highlights/Alle Fotos, Jahre, Tage): zeigt „gewählt“ über
/// <c>IsChecked</c>, schaltet beim Klick aber nicht selbst um — der Zustand kommt allein aus dem ViewModel (sonst wäre ein
/// erneuter Klick auf den gewählten Knopf optisch „abgewählt“, ohne dass sich im ViewModel etwas ändert).
/// </summary>
public sealed class StorySelectButton : ToggleButton
{
    protected override void OnToggle()
    {
    }
}
