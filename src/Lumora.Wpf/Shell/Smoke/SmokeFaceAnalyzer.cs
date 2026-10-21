using System;
using System.Threading;
using Lumora.Services.Faces;

namespace Lumora.Shell.Smoke;

/// <summary>
/// Rauchtest-Ersatz für den ONNX-Analysator (ADR-0035): braucht keine Modelle (die dürfen nicht ins Repo und nicht in
/// den Rauchtest), meldet je Bild genau ein Gesicht in der Bildmitte mit einem aus den Pixeln abgeleiteten,
/// L2-normierten Fingerabdruck und wartet je Bild <see cref="Delay"/> — lang genug, um Pause/Fortsetzen zu prüfen.
/// Das Bild selbst kommt über den echten WIC-Lader.
/// </summary>
internal sealed class SmokeFaceAnalyzerFactory : IFaceAnalyzerFactory
{
    public const string Version = "smoke-fake/v1";

    public static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(150);

    public string ModelVersion => Version;

    public bool IsAvailable => true;

    public IFaceAnalyzer Create(bool useGpu) => new SmokeFaceAnalyzer();

    private sealed class SmokeFaceAnalyzer : IFaceAnalyzer
    {
        public string ModelVersion => Version;

        public string ExecutionProvider => "Rauchtest";

        public FaceAnalysis Analyze(FaceImage image, CancellationToken ct = default)
        {
            Thread.Sleep(Delay);
            var w = image.Width;
            var h = image.Height;
            var side = Math.Min(w, h) / 3f;
            var x1 = (w - side) / 2;
            var y1 = (h - side) / 2;
            var landmarks = new[]
            {
                new FacePoint(x1 + side * 0.3f, y1 + side * 0.4f),
                new FacePoint(x1 + side * 0.7f, y1 + side * 0.4f),
                new FacePoint(x1 + side * 0.5f, y1 + side * 0.6f),
                new FacePoint(x1 + side * 0.35f, y1 + side * 0.8f),
                new FacePoint(x1 + side * 0.65f, y1 + side * 0.8f),
            };
            var detection = new FaceDetection(x1, y1, x1 + side, y1 + side, 0.9f, landmarks);
            var embedding = new float[FaceEmbeddingCodec.Dimensions];
            for (var i = 0; i < embedding.Length; i++)
            {
                embedding[i] = image.Pixels[(i * 7919) % image.Pixels.Length] + 1f;
            }

            FaceEmbeddingCodec.NormalizeL2(embedding);
            var quality = FaceQuality.Score(detection.Score, FaceQuality.FaceSize(detection), 100f);
            return new FaceAnalysis(w, h, [new AnalyzedFace(detection, embedding, quality)]);
        }

        public void Dispose()
        {
        }
    }
}
