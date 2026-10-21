namespace Lumora.Domain.Entities;

/// <summary>
/// Foto-Güte einer Datei (ADR-0057, Tabelle "FileQuality", Schlüssel FileId) — Grundlage für Highlights und Titelbild der
/// Storys. Aus dem Vorschaubild (Videos: nur daraus) im Hintergrund berechnet:
/// <see cref="Sharpness"/> = Laplace-Varianz der Helligkeit (Mittel der schärfsten Kacheln), logarithmisch auf 0..1
/// normiert; <see cref="Exposure"/> = 0..1 aus mittlerer Helligkeit und Anteil ausgefressener/abgesoffener Pixel (1 = gut);
/// <see cref="Aesthetic"/> = Bildwirkung 0..1 aus dem SigLIP-2-Bild-Vektor (<see cref="FileEmbedding"/>) gegen feste
/// Text-Paare, NULL ohne Vektor bzw. ohne Modell; <see cref="AestheticVersion"/> = Modell + Prompt-Satz, mit dem sie
/// gerechnet wurde. Gültig nur für die gelesene Dateiversion: <see cref="SourceSize"/> und <see cref="SourceModifiedAt"/>
/// (Unix-Sekunden wie <c>Files.ModifiedAtUtc</c>) müssen zur Files-Zeile passen und <see cref="Version"/> zum Verfahren,
/// sonst wird neu gerechnet. <see cref="ErrorCode"/> ≠ null = nicht lesbar (mit derselben Version kein neuer Versuch).
/// Files-Zeile weg → Zeile weg (ON DELETE CASCADE).
/// </summary>
public sealed class FileQuality
{
    public int FileId { get; set; }

    public double? Sharpness { get; set; }

    public double? Exposure { get; set; }

    public double? Aesthetic { get; set; }

    public string? AestheticVersion { get; set; }

    public int Version { get; set; }

    public long SourceSize { get; set; }

    public long SourceModifiedAt { get; set; }

    public string? ErrorCode { get; set; }
}
