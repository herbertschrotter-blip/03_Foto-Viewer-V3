using PhotoViewerV3.Services.Faces;
using PhotoViewerV3.Services.Faces.Indexing;

namespace PhotoViewerV3.Tests.Faces;

/// <summary>
/// Lader-Fake: dekodiert nichts. Die Zahl der Gesichter steht im Dateinamen („p2_…“ = 2 Gesichter, sonst 1) und wird
/// über die Bildbreite (100 + n) an den Analysator-Fake weitergegeben; „kaputt“ im Namen = nicht dekodierbar.
/// </summary>
public sealed class FakeFaceImageLoader : IFaceImageLoader
{
    private int _loads;

    public int Loads => Volatile.Read(ref _loads);

    public FaceImage Load(string path, int maxEdge)
    {
        Interlocked.Increment(ref _loads);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("fehlt", path);
        }

        var name = Path.GetFileName(path);
        if (name.Contains("kaputt", StringComparison.OrdinalIgnoreCase))
        {
            throw new FaceImageDecodeException("kaputt");
        }

        var faces = name.StartsWith('p') && name.Length > 1 && char.IsDigit(name[1]) ? name[1] - '0' : 1;
        var width = 100 + faces;
        var height = name.Contains("gift", StringComparison.OrdinalIgnoreCase) ? 99 : 100; // Analysator wirft
        return new FaceImage(new byte[width * height * 3], width, height);
    }
}

/// <summary>
/// Analysator-Fake: n Gesichter nebeneinander (n = Bildbreite − 100), Fingerabdruck = Einheitsvektor je Position.
/// Optional ein Tor (<see cref="Gate"/>), an dem jeder Aufruf wartet — damit Tests Pause und Abbruch genau steuern.
/// Zählt gleichzeitige Aufrufe (nie mehr als einer erlaubt).
/// </summary>
public sealed class FakeFaceAnalyzerFactory(string version = "fake/v1") : IFaceAnalyzerFactory
{
    private int _active;
    private int _maxActive;
    private int _calls;
    private int _created;

    public string ModelVersion { get; set; } = version;

    public bool IsAvailable { get; set; } = true;

    /// <summary>Wenn gesetzt, wartet jeder Analyse-Aufruf auf eine Freigabe (Release).</summary>
    public SemaphoreSlim? Gate { get; set; }

    public TimeSpan Delay { get; set; }

    public int Calls => Volatile.Read(ref _calls);

    public int MaxConcurrent => Volatile.Read(ref _maxActive);

    public int Created => Volatile.Read(ref _created);

    public bool? LastUseGpu { get; private set; }

    public IFaceAnalyzer Create(bool useGpu)
    {
        Interlocked.Increment(ref _created);
        LastUseGpu = useGpu;
        return new Analyzer(this, ModelVersion);
    }

    private sealed class Analyzer(FakeFaceAnalyzerFactory owner, string version) : IFaceAnalyzer
    {
        public string ModelVersion => version;

        public string ExecutionProvider => "Fake";

        public FaceAnalysis Analyze(FaceImage image, CancellationToken ct = default)
        {
            var now = Interlocked.Increment(ref owner._active);
            try
            {
                int seen;
                while (now > (seen = Volatile.Read(ref owner._maxActive)) && Interlocked.CompareExchange(ref owner._maxActive, now, seen) != seen)
                {
                }

                owner.Gate?.Wait(TimeSpan.FromSeconds(30));
                if (owner.Delay > TimeSpan.Zero)
                {
                    Thread.Sleep(owner.Delay);
                }

                Interlocked.Increment(ref owner._calls);
                if (image.Height == 99)
                {
                    throw new InvalidOperationException("Gift");
                }

                var count = image.Width - 100;
                var faces = new List<AnalyzedFace>(count);
                for (var k = 0; k < count; k++)
                {
                    var x = 5f + 30 * k;
                    var points = Enumerable.Range(0, 5).Select(i => new FacePoint(x + i, 10 + i)).ToArray();
                    var detection = new FaceDetection(x, 10, x + 20, 30, 0.9f, points);
                    var embedding = new float[FaceEmbeddingCodec.Dimensions];
                    embedding[k] = 1f;
                    faces.Add(new AnalyzedFace(detection, embedding, FaceQuality.Score(0.9f, 20, 100)));
                }

                return new FaceAnalysis(image.Width, image.Height, faces);
            }
            finally
            {
                Interlocked.Decrement(ref owner._active);
            }
        }

        public void Dispose()
        {
        }
    }
}

/// <summary>Einstellungen im Speicher (Tests ohne Settings-Tabelle).</summary>
public sealed class MemoryFaceSettingsStore(FaceIndexSettings initial) : IFaceIndexSettingsStore
{
    public FaceIndexSettings Current { get; private set; } = initial;

    public Task<FaceIndexSettings> LoadAsync(CancellationToken ct = default) => Task.FromResult(Current);

    public Task SaveAsync(FaceIndexSettings settings, CancellationToken ct = default)
    {
        Current = settings;
        return Task.CompletedTask;
    }
}
