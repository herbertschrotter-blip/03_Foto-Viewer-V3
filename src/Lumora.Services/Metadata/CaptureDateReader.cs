using Lumora.Domain.Enums;

namespace Lumora.Services.Metadata;

/// <summary>Liest das Aufnahmedatum einer Datei (Metadaten, sonst Dateiname) — austauschbar für Tests.</summary>
public interface ICaptureDateReader
{
    /// <summary>
    /// Datum aus den Metadaten (EXIF bzw. Video-Container), sonst aus dem Dateinamen; null = nichts gefunden
    /// (Herkunft <see cref="CaptureDateSource.Unknown"/>). Wirft nicht; die Datei muss lokal vorliegen (der Aufrufer
    /// prüft auf Cloud-Platzhalter, deren Lesen einen Download auslöste).
    /// </summary>
    CaptureDateValue? Read(string path);
}

/// <summary>
/// Rangfolge (ADR-0046): EXIF <c>DateTimeOriginal</c> bzw. Video-Container (<see cref="MediaDateReader"/>) vor dem
/// Dateinamen (<see cref="FileNameDateParser"/>). Archive und andere Formate liefern nur den Dateinamen.
/// </summary>
public sealed class CaptureDateReader : ICaptureDateReader
{
    public CaptureDateValue? Read(string path)
        => MediaDateReader.Read(path) ?? FileNameDateParser.Parse(Path.GetFileName(path));
}
