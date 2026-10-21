using PhotoViewerV3.Services.Faces;
using PhotoViewerV3.Services.Motifs;
using PhotoViewerV3.Services.Motifs.Siglip;

namespace PhotoViewerV3.Tests.Motifs;

/// <summary>
/// Encoder-Fake der Motiv-Erkennung (ADR-0052): Einheitsvektoren statt Modell. Texte mit „hund“/„dog“ → e1,
/// „katze“/„cat“ → e2, „strand“/„beach“ → e3, alles andere → e700 (orthogonal, keine Treffer). Bilder vom
/// <c>FakeFaceImageLoader</c> (Breite 100 + n aus dem Dateinamen „pN_…“, sonst n = 1) → e_n — also „a.jpg“ = Hund,
/// „p2_….jpg“ = Katze, „p3_….jpg“ = Strand. Optional ein Tor (<see cref="Gate"/>) und eine Ausführung („DirectML“ für die
/// GPU-Sperre); zählt gleichzeitige Bild-Läufe.
/// </summary>
public sealed class FakeMotifEncoderFactory(string version = "fake-motif/v1") : IMotifEncoderFactory
{
    private int _calls;
    private int _active;
    private int _maxActive;

    public string ModelVersion { get; set; } = version;

    public bool IsAvailable { get; set; } = true;

    public MotifScoring Scoring => MotifScoring.Siglip2Base;

    public SemaphoreSlim? Gate { get; set; }

    public string Provider { get; set; } = "Fake";

    /// <summary>Wird bei jedem Bild-Lauf mit der Ausführung aufgerufen (GPU-Sperren-Test).</summary>
    public Action? OnEncode { get; set; }

    public int Calls => Volatile.Read(ref _calls);

    public int MaxConcurrent => Volatile.Read(ref _maxActive);

    public int TextCalls { get; private set; }

    public IMotifImageEncoder CreateImageEncoder(bool useGpu) => new ImageEncoder(this);

    public IMotifTextEncoder CreateTextEncoder() => new TextEncoder(this);

    public static float[] Unit(int index)
    {
        var v = new float[SiglipModel.Dimensions];
        v[index] = 1f;
        return v;
    }

    public static float[] TextVector(string text)
    {
        var t = text.ToLowerInvariant();
        return t.Contains("hund") || t.Contains("dog") ? Unit(1)
            : t.Contains("katze") || t.Contains("cat") ? Unit(2)
            : t.Contains("strand") || t.Contains("beach") ? Unit(3)
            : Unit(700);
    }

    private sealed class ImageEncoder(FakeMotifEncoderFactory owner) : IMotifImageEncoder
    {
        public string ModelVersion => owner.ModelVersion;

        public string ExecutionProvider => owner.Provider;

        public float[] EncodeImage(FaceImage image)
        {
            var now = Interlocked.Increment(ref owner._active);
            try
            {
                int seen;
                while (now > (seen = Volatile.Read(ref owner._maxActive)) && Interlocked.CompareExchange(ref owner._maxActive, now, seen) != seen)
                {
                }

                owner.Gate?.Wait(TimeSpan.FromSeconds(30));
                owner.OnEncode?.Invoke();
                Interlocked.Increment(ref owner._calls);
                if (image.Height == 99)
                {
                    throw new InvalidOperationException("Gift");
                }

                return Unit(Math.Clamp(image.Width - 100, 0, 699));
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

    private sealed class TextEncoder(FakeMotifEncoderFactory owner) : IMotifTextEncoder
    {
        public string ModelVersion => owner.ModelVersion;

        public IReadOnlyList<float[]> EncodeTexts(IReadOnlyList<string> texts)
        {
            owner.TextCalls++;
            return texts.Select(TextVector).ToList();
        }

        public void Dispose()
        {
        }
    }
}

/// <summary>Einstellungen im Speicher.</summary>
public sealed class MemoryMotifSettingsStore(MotifSettings initial) : IMotifSettingsStore
{
    public MotifSettings Current { get; private set; } = initial;

    public Task<MotifSettings> LoadAsync(CancellationToken ct = default) => Task.FromResult(Current);

    public Task SaveAsync(MotifSettings settings, CancellationToken ct = default)
    {
        Current = settings;
        return Task.CompletedTask;
    }
}
