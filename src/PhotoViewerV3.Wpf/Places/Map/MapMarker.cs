using System.Collections.Generic;
using System.Globalization;
using System.Windows.Media;
using PhotoViewerV3.Services.Places.Clustering;

namespace PhotoViewerV3.Places.Map;

/// <summary>
/// Ein Marker auf der Karte (ADR-0051): Gruppe mit Vorschaubild des besten Fotos und Anzahl-Plakette oder ein einzelnes
/// Foto als kleines Vorschaubild. <see cref="Image"/> setzt das ViewModel, sobald das Vorschaubild bereitsteht.
/// </summary>
public sealed class MapMarker
{
    public MapMarker(GeoCluster cluster, GeoClusterIndex index, ImageSource? image = null)
    {
        Cluster = cluster;
        Index = index;
        Image = image;
    }

    public GeoCluster Cluster { get; }

    /// <summary>Index, aus dem der Marker stammt (Dateien/Rahmen immer dort nachschlagen — ein Neuladen ersetzt den Index).</summary>
    public GeoClusterIndex Index { get; }

    /// <summary>Mittelpunkt liegt im sichtbaren Ausschnitt (nicht nur im Rand für halb sichtbare Marker).</summary>
    public bool IsInView { get; init; }

    public IReadOnlyList<int> FileIds() => Index.GetFileIds(Cluster);

    public double X => Cluster.X;

    public double Y => Cluster.Y;

    public int Count => Cluster.Count;

    public int BestFileId => Cluster.BestFileId;

    public bool IsGroup => Cluster.Count > 1;

    public ImageSource? Image { get; set; }

    /// <summary>Plakette: „12“, „1.234“, ab 10.000 „12 Tsd.“.</summary>
    public string CountText => FormatCount(Count);

    public string AutomationName => IsGroup ? $"Gruppe mit {Count.ToString("N0", Culture)} Fotos" : "Foto";

    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("de-DE");

    public static string FormatCount(int count)
        => count >= 10_000 ? $"{(count / 1000).ToString("N0", Culture)} Tsd." : count.ToString("N0", Culture);
}
