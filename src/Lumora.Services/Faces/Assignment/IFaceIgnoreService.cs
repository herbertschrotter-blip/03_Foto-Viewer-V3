using System.Drawing;

namespace Lumora.Services.Faces.Assignment;

/// <summary>
/// Ignorierte Gesichter (ADR-0040): ansehen und zurückholen, ganze Gruppe ignorieren, Mindestgröße für Gesichter
/// („kleiner als … Pixel automatisch ignorieren“, <see cref="FaceAutoIgnore"/>). Implementiert vom Zuordnungsdienst
/// (<see cref="FaceRecognitionService"/>) — er bleibt der einzige Schreiber der Zustände 1–3 (ADR-0037). Ein eigenes
/// Interface, damit die Fakes von <see cref="IFaceRecognitionService"/> (Galerie, Lightbox) unverändert bleiben.
/// </summary>
public interface IFaceIgnoreService
{
    /// <summary>
    /// Ignorierte Gesichter der aktiven Bibliothek seitenweise (neueste Fotos zuerst), mit Gesamtzahl und Anzahl der
    /// automatisch (zu klein) ignorierten.
    /// </summary>
    Task<IgnoredFacesPage> GetIgnoredFacesAsync(int offset, int limit, CancellationToken ct = default);

    /// <summary>
    /// „Zurückholen“: ignorierte Gesichter (manuell oder automatisch) werden wieder unbestätigt (Zustand 0); danach
    /// werden Vorschläge neu berechnet und Gruppen neu gebildet. Liefert die Zahl der zurückgeholten Gesichter.
    /// Ein zurückgeholtes zu kleines Gesicht bleibt, bis die Schwelle geändert wird.
    /// </summary>
    Task<int> UnignoreFacesAsync(IReadOnlyCollection<long> faceIds, CancellationToken ct = default);

    /// <summary>
    /// Ignoriert ALLE Mitglieder der zwischengespeicherten Gruppe — auch die abgewählten Ausreißer — soweit sie noch
    /// offen sind (0/1); die Gruppe verschwindet. 0, wenn es die Gruppe nicht mehr gibt.
    /// </summary>
    Task<int> IgnoreGroupAsync(long groupId, CancellationToken ct = default);

    /// <summary>Schwelle in Pixeln (0 = aus), Standard <see cref="FaceAutoIgnore.DefaultMinSize"/>.</summary>
    Task<int> GetMinFaceSizeAsync(CancellationToken ct = default);

    /// <summary>
    /// Speichert die Schwelle; der Hintergrund-Worker wendet sie danach auf alle vorhandenen Gesichter an (zu kleine
    /// offene ignorieren, nicht mehr betroffene automatisch ignorierte zurückholen). Neue Gesichter ignoriert der Index
    /// gleich beim Einfügen.
    /// </summary>
    Task SetMinFaceSizeAsync(int pixels, CancellationToken ct = default);
}

/// <summary>Ein ignoriertes Gesicht (für die Ansicht „Ignorierte Gesichter“).</summary>
/// <param name="Size">Seite der Box in Pixeln des analysierten Bildes.</param>
/// <param name="IsAutomatic">true = automatisch wegen der Mindestgröße ignoriert („zu klein“).</param>
public sealed record IgnoredFaceInfo(long FaceId, int FileId, RectangleF BoxRelative, float Size, bool IsAutomatic);

/// <summary>Eine Seite ignorierter Gesichter.</summary>
/// <param name="Total">Alle ignorierten Gesichter der aktiven Bibliothek.</param>
/// <param name="AutomaticCount">Davon automatisch (zu klein) ignoriert.</param>
public sealed record IgnoredFacesPage(IReadOnlyList<IgnoredFaceInfo> Faces, int Total, int AutomaticCount)
{
    public static IgnoredFacesPage Empty { get; } = new([], 0, 0);
}
