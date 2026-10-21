using CommunityToolkit.Mvvm.ComponentModel;

namespace PhotoViewerV3.Gallery.ViewModels;

/// <summary>Motive einer Datei (ADR-0052) für die Listenspalte „Motive“ und den Kachel-Tooltip; wird als Ganzes ersetzt.</summary>
public sealed partial class GalleryItem
{
    /// <summary>„Strand, Meer, Sonnenuntergang“ (wahrscheinlichste zuerst); leer ohne Treffer.</summary>
    [ObservableProperty] private string _motifsText = string.Empty;

    /// <summary>Gegen ältere Ergebnisse bei überlappenden Ladevorgängen (wie Personen).</summary>
    internal long MotifsSequence { get; set; }
}
