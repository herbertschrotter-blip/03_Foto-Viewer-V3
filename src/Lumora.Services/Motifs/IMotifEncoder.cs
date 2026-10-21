using Lumora.Services.Faces;

namespace Lumora.Services.Motifs;

/// <summary>
/// Bild-Encoder der Motiv-Erkennung (ADR-0052): ein Bild → L2-normierter Bild-Vektor. Eine Instanz hält das geladene
/// Bildmodell (Grafikkarte über DirectML oder Prozessor), ist thread-sicher (Läufe serialisiert) und wird mit
/// <see cref="IDisposable.Dispose"/> freigegeben.
/// </summary>
public interface IMotifImageEncoder : IDisposable
{
    /// <summary>Kennung von Modell und Vorverarbeitung; ändert sie sich, werden alle Bilder neu analysiert.</summary>
    string ModelVersion { get; }

    /// <summary>Tatsächlich genutzte Ausführung („DirectML“, „CPU“, „Fake“ …).</summary>
    string ExecutionProvider { get; }

    float[] EncodeImage(FaceImage image);
}

/// <summary>
/// Text-Encoder der Motiv-Erkennung: Texte → L2-normierte Text-Vektoren im selben Raum wie die Bild-Vektoren. Läuft auf
/// dem Prozessor (nur beim Anlegen/Ändern von Begriffen und bei der Freitext-Suche).
/// </summary>
public interface IMotifTextEncoder : IDisposable
{
    string ModelVersion { get; }

    IReadOnlyList<float[]> EncodeTexts(IReadOnlyList<string> texts);
}

/// <summary>Erzeugt die Encoder aus den Modell-Dateien (nach dem Download, nach Umschalten Grafikkarte/CPU).</summary>
public interface IMotifEncoderFactory
{
    /// <summary>Kennung, die mit diesen Modellen erzeugte Encoder melden (ohne die Modelle zu laden).</summary>
    string ModelVersion { get; }

    /// <summary>true, wenn die Modell-Dateien vorhanden sind.</summary>
    bool IsAvailable { get; }

    /// <summary>Wahrscheinlichkeit aus Kosinus (SigLIP: Sigmoid mit logit_scale/logit_bias des Modells).</summary>
    MotifScoring Scoring { get; }

    IMotifImageEncoder CreateImageEncoder(bool useGpu);

    IMotifTextEncoder CreateTextEncoder();
}
