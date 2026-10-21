using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Lumora.Slideshow;

/// <summary>
/// Diashow für beliebige Dateilisten (ADR-0059): Galerie (Auswahl oder ganze Ansicht), Album, Lightbox („Diashow ab
/// hier“) und später Storys. Öffnet ein eigenes, rahmenloses Vollbild-Fenster auf dem Bildschirm des Hauptfensters; ein
/// zweiter Start ersetzt eine laufende Diashow. Keine Musik (Herbert). Aufrufe vom UI-Thread oder von beliebigen Threads.
/// </summary>
public interface ISlideshowService
{
    /// <summary>Startet die Diashow über <paramref name="fileIds"/> (Reihenfolge wie übergeben bzw. zufällig laut Einstellung).</summary>
    /// <param name="startIndex">Erstes Bild (Index in <paramref name="fileIds"/>); bei zufälliger Reihenfolge kommt es zuerst.</param>
    /// <param name="title">Kurzer Titel oben rechts (z. B. Albumname); null = keiner.</param>
    void Start(IReadOnlyList<int> fileIds, int startIndex = 0, string? title = null);

    /// <summary>Wie <see cref="Start(IReadOnlyList{int}, int, string?)"/>, aber die Ids kommen seitenweise (ganze Galerie-Ansicht).</summary>
    void Start(ISlideshowSequence sequence, int startIndex = 0, string? title = null);

    /// <summary>True, solange ein Diashow-Fenster offen ist.</summary>
    bool IsOpen { get; }
}

/// <summary>
/// Reihenfolge der Dateien einer Diashow. Die Diashow fragt die Ids blockweise ab (nie alle auf einmal), damit auch eine
/// Galerie-Ansicht mit 500 000 Treffern nur die gerade gebrauchten Seiten lädt.
/// </summary>
public interface ISlideshowSequence
{
    /// <summary>Anzahl der Einträge (fest für die Dauer der Diashow).</summary>
    int Count { get; }

    /// <summary>
    /// Ids der Einträge <paramref name="start"/> … <paramref name="start"/> + <paramref name="count"/> − 1. Darf kürzer sein
    /// (Katalog hat sich inzwischen geändert); fehlende Einträge — auch eine Id ≤ 0 als Platzhalter — überspringt die Diashow.
    /// </summary>
    Task<IReadOnlyList<int>> GetRangeAsync(int start, int count, CancellationToken ct);
}
