using Lumora.Services.Faces;
using Lumora.Services.Faces.Alignment;
using Lumora.Services.Faces.Onnx;

namespace Lumora.Tests.Faces;

/// <summary>Nur mit echten Modellen (<c>LUMORA_FACE_MODELS</c> zeigt auf buffalo_l), sonst übersprungen.</summary>
public sealed class FaceModelFactAttribute : FactAttribute
{
    public FaceModelFactAttribute()
    {
        var folder = Environment.GetEnvironmentVariable("LUMORA_FACE_MODELS");
        if (string.IsNullOrWhiteSpace(folder)
            || !File.Exists(Path.Combine(folder, "det_10g.onnx"))
            || !File.Exists(Path.Combine(folder, "w600k_r50.onnx")))
        {
            Skip = "Modelle nicht vorhanden (LUMORA_FACE_MODELS auf den buffalo_l-Ordner setzen).";
        }
    }
}

/// <summary>
/// ONNX-Analysator mit den echten Modellen (ADR-0035) — ohne Fotos: Ein-/Ausgaben passen zur erwarteten Form,
/// CPU und DirectML laden, ein leeres Bild hat keine Gesichter, ein Fingerabdruck hat 512 Werte mit Länge 1, und
/// derselbe Ausschnitt liefert auf CPU und DirectML (fast) denselben Fingerabdruck.
/// </summary>
public sealed class OnnxFaceAnalyzerModelTests
{
    private static string Folder => Environment.GetEnvironmentVariable("LUMORA_FACE_MODELS")!;

    [FaceModelFact]
    public void Cpu_und_DirectML_laden_leeres_Bild_ohne_Gesicht_Fingerabdruck_normiert_und_gleich()
    {
        using var cpu = OnnxFaceAnalyzer.Load(Path.Combine(Folder, "det_10g.onnx"), Path.Combine(Folder, "w600k_r50.onnx"), useGpu: false);
        using var gpu = OnnxFaceAnalyzer.Load(Path.Combine(Folder, "det_10g.onnx"), Path.Combine(Folder, "w600k_r50.onnx"), useGpu: true);
        cpu.ExecutionProvider.Should().Be("CPU");
        gpu.ExecutionProvider.Should().BeOneOf("DirectML", "CPU");
        cpu.ModelVersion.Should().Be(OnnxFaceAnalyzer.Version);

        var gray = new FaceImage(Enumerable.Repeat((byte)128, 800 * 600 * 3).ToArray(), 800, 600);
        cpu.Analyze(gray).Faces.Should().BeEmpty();
        gpu.Analyze(gray).Faces.Should().BeEmpty();

        var crop = new byte[FaceAligner.CropSize * FaceAligner.CropSize * 3];
        for (var i = 0; i < crop.Length; i++)
        {
            crop[i] = (byte)(i * 31 % 251);
        }

        var a = cpu.Embed([crop, crop])[0];
        var batch = cpu.Embed([crop, crop]);
        var b = gpu.Embed([crop])[0];

        a.Should().HaveCount(512);
        a.Sum(v => v * v).Should().BeApproximately(1f, 1e-4f);
        FaceEmbeddingCodec.Cosine(batch[0], batch[1]).Should().BeApproximately(1f, 1e-4f, "Stapel liefert für gleiche Eingaben gleiche Werte");
        FaceEmbeddingCodec.Cosine(a, b).Should().BeGreaterThan(0.99f, "CPU und DirectML rechnen dasselbe Modell");
    }
}
