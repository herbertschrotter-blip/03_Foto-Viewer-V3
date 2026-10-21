using System;
using System.Windows;
using System.Windows.Controls;

namespace Lumora.Gallery.Controls;

/// <summary>
/// Filterkarte („Datei“, „Inhalt“, „Es gilt“) als eigenes Steuerelement (ADR-0055): Galerie- und Ordner-Seite (über
/// <see cref="GalleryToolbar"/>) und die Seite „Orte“ binden dieselbe Karte an das Singleton-<c>GalleryViewModel</c> — ein
/// Filterzustand für alle drei Seiten. Reines Layout.
/// </summary>
public partial class GalleryFilterCard : UserControl
{
    public static readonly DependencyProperty ShowHitCountProperty = DependencyProperty.Register(
        nameof(ShowHitCount),
        typeof(bool),
        typeof(GalleryFilterCard),
        new PropertyMetadata(true));

    public GalleryFilterCard()
    {
        InitializeComponent();
    }

    /// <summary>„→ n Treffer“ in der „Es gilt“-Zeile (Galerie: ja; Karte: nein, sie zählt selbst).</summary>
    public bool ShowHitCount
    {
        get => (bool)GetValue(ShowHitCountProperty);
        set => SetValue(ShowHitCountProperty, value);
    }

    /// <summary>Eigene Gruppe für das Dateiart-Segment je Instanz (RadioButtons gleicher Gruppe schalten sich sonst gegenseitig).</summary>
    public string MediaGroupName { get; } = "GalleryMedia-" + Guid.NewGuid().ToString("N");
}
