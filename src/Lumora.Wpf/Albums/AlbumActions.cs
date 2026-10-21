using Lumora.Services.Albums;
using Lumora.Shell;

namespace Lumora.Albums;

/// <summary>
/// Bündelt, was Galerie, Ordner-Seite und Seite „Alben“ für Alben und Ordner-Verknüpfungen brauchen (ADR-0054): die beiden
/// Dienste und die Dialoge (Namen eingeben, bestätigen, Zielordner wählen). Ein Parameter im Konstruktor der Galerie
/// statt drei — die geteilten Konstruktoren bleiben klein.
/// </summary>
public sealed class AlbumActions
{
    public AlbumActions(IAlbumService albums, IFolderLinkService links, IDialogService dialogs)
    {
        Albums = albums;
        Links = links;
        Dialogs = dialogs;
    }

    public IAlbumService Albums { get; }

    public IFolderLinkService Links { get; }

    public IDialogService Dialogs { get; }
}
