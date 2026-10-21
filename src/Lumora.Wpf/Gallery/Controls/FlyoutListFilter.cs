using System;
using System.Windows.Controls;

namespace Lumora.Gallery.Controls;

/// <summary>
/// Suchfeld in den Auswahl-Flyouts der Galerie (Tags, Personen; 0.2.43): blendet Einträge aus, deren Name den Text nicht
/// enthält (ohne Groß-/Kleinschreibung). Leerer Text hebt den Filter auf; beim Schließen des Flyouts wieder aufheben,
/// weil die Ansicht der Liste mit der zweiten GalleryView-Instanz geteilt ist.
/// </summary>
public static class FlyoutListFilter
{
    public static void Apply<T>(ItemsControl list, string? text, Func<T, string> nameOf)
    {
        var needle = text?.Trim();
        list.Items.Filter = string.IsNullOrEmpty(needle)
            ? null
            : item => item is T entry && nameOf(entry).Contains(needle, StringComparison.CurrentCultureIgnoreCase);
    }

    public static void Clear(ItemsControl list) => list.Items.Filter = null;
}
