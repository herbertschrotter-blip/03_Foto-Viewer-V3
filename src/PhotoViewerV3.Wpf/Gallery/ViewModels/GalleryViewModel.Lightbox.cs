using System.Collections.Generic;
using PhotoViewerV3.Lightbox;

namespace PhotoViewerV3.Gallery.ViewModels;

/// <summary>Lightbox-Sequenz aus der aktuellen Page (Doppelklick/Enter auf eine Kachel oder Listenzeile).</summary>
public sealed partial class GalleryViewModel
{
    /// <summary>Wie das geöffnete Item in der Page gefunden wurde (<see cref="BuildLightboxSequence"/>).</summary>
    internal enum LightboxStartMatch
    {
        /// <summary>Dieselbe Instanz steht in der Page.</summary>
        Reference,

        /// <summary>Andere Instanz mit derselben FileId (z. B. nach einem Seiten-Reload mit neuen Items).</summary>
        Id,

        /// <summary>Nicht in der Page — die Sequenz enthält nur das Item selbst.</summary>
        NotInPage,
    }

    /// <summary>
    /// Baut die Lightbox-Einträge für <paramref name="item"/>: alle Items der Page, Start = Position des Items — erst per
    /// Referenz, dann per FileId. Steht es gar nicht in der Page, öffnet die Lightbox nur dieses eine Item statt
    /// still bei Index 0 zu beginnen (0.2.42: sonst zeigte sie eine andere Datei als die doppelgeklickte).
    /// </summary>
    internal static (List<LightboxEntry> Entries, int StartIndex, LightboxStartMatch Match) BuildLightboxSequence(
        IReadOnlyList<GalleryItem> page, GalleryItem item)
    {
        var referenceIndex = -1;
        var idIndex = -1;
        var entries = new List<LightboxEntry>(page.Count);
        for (var i = 0; i < page.Count; i++)
        {
            var it = page[i];
            if (referenceIndex < 0 && ReferenceEquals(it, item))
            {
                referenceIndex = i;
            }
            else if (idIndex < 0 && it.Id == item.Id)
            {
                idIndex = i;
            }

            entries.Add(ToLightboxEntry(it));
        }

        if (referenceIndex >= 0)
        {
            return (entries, referenceIndex, LightboxStartMatch.Reference);
        }

        if (idIndex >= 0)
        {
            return (entries, idIndex, LightboxStartMatch.Id);
        }

        return (new List<LightboxEntry> { ToLightboxEntry(item) }, 0, LightboxStartMatch.NotInPage);
    }

    private static LightboxEntry ToLightboxEntry(GalleryItem it)
        => new(it.Id, it.SourcePath, it.Name, it.MediaType, it.IsFavorite, it.Rating, it.Size, it.SourceModifiedAtUtc);
}
