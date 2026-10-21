using CommunityToolkit.Mvvm.ComponentModel;
using PhotoViewerV3.Gallery.Data;

namespace PhotoViewerV3.Gallery.ViewModels;

/// <summary>
/// Galerie-Filter „Doppelte“ (ADR-0053, Chip in der Zeile „Inhalt“ — gleicher Inhalt; in „Datei“ bräche die Zeile bei 1280 DIP um): nur Dateien, zu denen es im aktuellen
/// Bereich eine byte-gleiche Datei gibt. UND-verknüpft mit allen anderen Filtern; in „Es gilt“ als „Doppelte (exakt
/// gleich)“ mit eigenem ✕; „Filter zurücksetzen“ hebt ihn auf. Aufräumen geht auf der Seite „Doppelte“.
/// </summary>
public sealed partial class GalleryViewModel
{
    /// <summary>Umschalter „Doppelte“.</summary>
    [ObservableProperty] private bool _duplicatesOnly;

    /// <summary>Text in der Zeile „Es gilt“.</summary>
    internal const string DuplicatesConditionLabel = "Doppelte (exakt gleich)";

    /// <summary>Leertext, wenn der Filter nichts findet.</summary>
    internal const string DuplicatesEmptyMessage = "Keine doppelten Dateien — oder die Prüfsummen werden gerade noch im Hintergrund berechnet.";

    /// <summary>Filter mit dem Bereich der Galerie (frisch je Abfrage); null ohne Filter.</summary>
    private GalleryDuplicateFilter? DuplicateFilter => DuplicatesOnly ? new GalleryDuplicateFilter(ScopeLibraryIds) : null;

    partial void OnDuplicatesOnlyChanged(bool value) => OnFileFilterPartChanged();
}
