using System.Threading.Tasks;
using System.Windows;

namespace Lumora.Gallery.Places;

/// <summary>
/// Haken für die Kartenauswahl im Dialog „Ort setzen“ (ADR-0050): die Karte (Agent W) implementiert und registriert ihn
/// (<c>services.AddSingleton&lt;IMapPicker, …&gt;()</c>). Ohne Registrierung blendet der Dialog den Knopf „Auf der Karte
/// wählen…“ aus.
/// </summary>
public interface IMapPicker
{
    /// <summary>
    /// Karte zum Wählen eines Punktes zeigen, zentriert auf <paramref name="initialLatitude"/>/<paramref name="initialLongitude"/>
    /// (null = Übersicht). Liefert die gewählte Koordinate oder null bei Abbruch.
    /// </summary>
    Task<(double Latitude, double Longitude)?> PickAsync(double? initialLatitude, double? initialLongitude, Window? owner);
}
