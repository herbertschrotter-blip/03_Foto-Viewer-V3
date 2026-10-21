using System.Drawing;

namespace Lumora.Services.Faces.Crops;

/// <summary>Ein Gesichts-Ausschnitt, der aus einer Quelldatei geschnitten werden soll.</summary>
/// <param name="BoxRelative">Box relativ (0..1) zum orientierten Bild.</param>
/// <param name="TargetPath">Ziel-JPEG (wird atomar über .tmp geschrieben).</param>
public sealed record FaceCropTarget(long FaceId, RectangleF BoxRelative, string TargetPath);

/// <summary>Ein Ausschnitt liegt im Cache bereit (vom Worker-Thread gemeldet).</summary>
public sealed record FaceCropReadyEventArgs(int LibraryId, long FaceId, string Path);

/// <summary>Feste Werte der Gesichts-Ausschnitte (ADR-0037).</summary>
public static class FaceCropDefaults
{
    /// <summary>Kantenlänge des quadratischen Ausschnitts in Pixeln.</summary>
    public const int Size = 128;

    /// <summary>Rand um die Box je Seite, als Anteil der längeren Box-Seite (Stirn, Kinn, etwas Hintergrund).</summary>
    public const float Margin = 0.3f;

    /// <summary>Unterordner im Thumbnail-Ordner der Bibliothek.</summary>
    public const string FolderName = "faces";
}

/// <summary>
/// Schneidet Gesichts-Ausschnitte aus einer Bilddatei (WIC im Wpf-Projekt, wie der Thumbnail-Renderer — Services bleibt
/// WPF-frei). Dekodiert die Datei einmal für alle Ausschnitte, wendet die EXIF-Orientierung an. Wirft bei nicht
/// lesbarer Datei.
/// </summary>
public interface IFaceCropRenderer
{
    void Render(string sourcePath, IReadOnlyList<FaceCropTarget> targets, int size);
}

/// <summary>
/// Cache der Gesichts-Ausschnitte für die Personen-Seite (ADR-0037): <c>thumbs\{libraryId}\faces\{faceId}.jpg</c>,
/// 128 × 128. Anforderungen werden im Hintergrund gebündelt (je Quelldatei ein Dekodieren), fertige Ausschnitte über
/// <see cref="CropReady"/> gemeldet (Worker-Thread). Ändern sich die Gesichter einer Datei (Index), werden ihre
/// Ausschnitte verworfen; Ausschnitte gelöschter Gesichter räumt ein entprellter Abgleich weg.
/// </summary>
public interface IFaceCropService
{
    event Action<FaceCropReadyEventArgs>? CropReady;

    string GetCropPath(int libraryId, long faceId);

    /// <summary>Ausschnitte anfordern; vorhandene werden sofort (im Hintergrund) gemeldet, fehlende erzeugt.</summary>
    void Request(int libraryId, IReadOnlyCollection<long> faceIds);

    /// <summary>Ausschnitte verwerfen (Gesicht gelöscht oder Box geändert).</summary>
    void Delete(int libraryId, IEnumerable<long> faceIds);

    /// <summary>Alle Ausschnitte aller Bibliotheken löschen (Datenschutz: Gesichtsdaten entfernen).</summary>
    void DeleteAll();

    /// <summary>Nachrichten abonnieren (einmal beim App-Start).</summary>
    void Start();
}
