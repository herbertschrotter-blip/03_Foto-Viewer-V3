using CommunityToolkit.Mvvm.ComponentModel;

namespace PhotoViewerV3.Gallery.ViewModels;

/// <summary>Ordner-Verknüpfung (ADR-0054): die Datei erscheint in der Ordneransicht nur über eine Verknüpfung.</summary>
public sealed partial class GalleryItem
{
    /// <summary>Ketten-Symbol auf Kachel, Collage und Liste; Kachel bleibt ein Automation-Blatt (Name = Dateiname).</summary>
    [ObservableProperty] private bool _isLinked;
}
