namespace Lumora.Services.Faces;

/// <summary>
/// Lädt ein Bild für die Gesichtserkennung (ADR-0035): RGB, EXIF-Orientierung angewandt, längste Kante höchstens
/// <c>maxEdge</c> (kleinere Bilder werden nicht vergrößert). Die Implementierung lebt im Wpf-Projekt (WIC), weil
/// Services kein WPF referenzieren darf (CLAUDE.md). Wirft <see cref="FileNotFoundException"/>, wenn die Datei fehlt,
/// und <see cref="FaceImageDecodeException"/>, wenn sie nicht dekodierbar ist. Muss von mehreren Threads gleichzeitig
/// aufrufbar sein.
/// </summary>
public interface IFaceImageLoader
{
    FaceImage Load(string path, int maxEdge);
}

/// <summary>Die Datei konnte nicht als Bild dekodiert werden.</summary>
public sealed class FaceImageDecodeException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Erkennt Gesichter und berechnet ihre Fingerabdrücke (ADR-0035). Eine Instanz hält geladene Modelle; sie ist
/// thread-sicher (gleichzeitige Aufrufe werden je Modell serialisiert) und wird mit <see cref="IDisposable.Dispose"/>
/// freigegeben.
/// </summary>
public interface IFaceAnalyzer : IDisposable
{
    /// <summary>Kennung von Modell und Vorverarbeitung; ändert sie sich, wird neu indiziert (<c>FaceScans.ModelVersion</c>).</summary>
    string ModelVersion { get; }

    /// <summary>Tatsächlich genutzte Ausführung („DirectML“, „CPU“, „Fake“ …) für Statusleiste und Einstellungen.</summary>
    string ExecutionProvider { get; }

    FaceAnalysis Analyze(FaceImage image, CancellationToken ct = default);
}

/// <summary>Erzeugt Analysatoren (nach Modell-Download, nach Umschalten Grafikkarte/CPU).</summary>
public interface IFaceAnalyzerFactory
{
    /// <summary>Modell-Version, die ein mit diesen Modellen erzeugter Analysator meldet (ohne die Modelle zu laden).</summary>
    string ModelVersion { get; }

    /// <summary>true, wenn die Modelle vorhanden sind und <see cref="Create"/> gelingen kann.</summary>
    bool IsAvailable { get; }

    /// <summary>Lädt die Modelle; <paramref name="useGpu"/> = DirectML mit Rückfall auf CPU. Wirft, wenn die Modelle fehlen.</summary>
    IFaceAnalyzer Create(bool useGpu);
}
