using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Domain.Entities;
using Lumora.Services.Database;
using Lumora.Services.Motifs;

namespace Lumora.Services.Quality;

/// <summary>
/// Bildwirkung ohne eigenes Modell (ADR-0057): Zero-Shot über die vorhandenen SigLIP-2-Bild-Vektoren der Motiv-Erkennung
/// (<c>FileEmbeddings</c>, ADR-0052). Mehrere positive und negative Beschreibungen (deutsch und englisch gemischt — SigLIP 2
/// ist mehrsprachig) werden je Seite gemittelt; Wert = σ((cos⁺ − cos⁻ − <see cref="Center"/>) · <see cref="Slope"/>).
/// Ändert sich ein Text, <see cref="PromptVersion"/> erhöhen (alle Werte werden neu gerechnet).
/// </summary>
public static class AestheticPrompts
{
    /// <summary>Version des Prompt-Satzes und der Abbildung; Teil von <c>FileQuality.AestheticVersion</c>.</summary>
    public const int PromptVersion = 1;

    /// <summary>Lage der Abbildung (Differenz der Kosinus-Werte, ab der ein Foto „mittel“ ist) — aus der Messung (Spike 16).</summary>
    public const double Center = -0.0045;

    /// <summary>Steilheit der Abbildung — aus der Messung (Spike 16: p10 → ≈ 0,2, p90 → ≈ 0,8).</summary>
    public const double Slope = 105.0;

    /// <summary>Positive Beschreibungen (Spike 16: von sieben verglichenen Sätzen der beste — weichgezeichnete Fassungen in
    /// 100 %, abgedunkelte in 98 % der Fälle niedriger).</summary>
    public static IReadOnlyList<string> Positive { get; } =
    [
        "a beautiful, sharp, well-exposed photo",
        "ein schönes, scharfes, gut belichtetes Foto",
        "ein gelungenes Urlaubsfoto mit schönen Farben",
    ];

    public static IReadOnlyList<string> Negative { get; } =
    [
        "a blurry, dark, failed photo",
        "ein verschwommenes, dunkles, missglücktes Foto",
    ];

    /// <summary>Kennung für <c>FileQuality.AestheticVersion</c>.</summary>
    public static string VersionFor(string modelVersion) => modelVersion + "|aesthetic-" + PromptVersion.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Gemittelte Text-Vektoren der Bildwirkung für ein Modell.</summary>
public sealed class AestheticModel
{
    public AestheticModel(string embeddingModelVersion, float[] positive, float[] negative)
    {
        if (positive.Length != negative.Length || positive.Length == 0)
        {
            throw new ArgumentException("Text-Vektoren unterschiedlicher Länge.");
        }

        EmbeddingModelVersion = embeddingModelVersion;
        Version = AestheticPrompts.VersionFor(embeddingModelVersion);
        Positive = positive;
        Negative = negative;
    }

    /// <summary>Modell der Bild-Vektoren (<c>FileEmbeddings.ModelVersion</c>), zu denen die Text-Vektoren passen.</summary>
    public string EmbeddingModelVersion { get; }

    /// <summary>Kennung für <c>FileQuality.AestheticVersion</c>.</summary>
    public string Version { get; }

    public float[] Positive { get; }

    public float[] Negative { get; }

    /// <summary>Differenz cos⁺ − cos⁻ eines normierten Bild-Vektors (Messung).</summary>
    public double Margin(ReadOnlySpan<float> image) => MotifVectors.Dot(image, Positive) - MotifVectors.Dot(image, Negative);

    /// <summary>Bildwirkung 0..1.</summary>
    public double Score(ReadOnlySpan<float> image)
        => 1.0 / (1.0 + Math.Exp(-(Margin(image) - AestheticPrompts.Center) * AestheticPrompts.Slope));

    /// <summary>Bild-Vektor aus dem BLOB (<c>FileEmbeddings.Embedding</c>: float16, ältere Zeilen ggf. float32); null bei falscher Länge.</summary>
    public float[]? DecodeImage(byte[] blob)
    {
        if (blob.Length == Positive.Length * 2)
        {
            return MotifVectors.DecodeHalf(blob);
        }

        return blob.Length == Positive.Length * sizeof(float) ? MotifVectors.Decode(blob) : null;
    }
}

/// <summary>Liefert die Text-Vektoren der Bildwirkung (einmal gerechnet, im Katalog zwischengespeichert).</summary>
public interface IQualityAesthetics
{
    /// <summary>Modell, dessen Bild-Vektoren gelten (<c>FileEmbeddings.ModelVersion</c>) — ohne das Modell zu laden.</summary>
    string EmbeddingModelVersion { get; }

    /// <summary>
    /// Text-Vektoren zum aktuellen Motiv-Modell; null, wenn es keins gibt (dann bleibt <c>Aesthetic</c> NULL). Rechnet sie
    /// beim ersten Bedarf mit dem Sprachmodell (Prozessor, einige Sekunden) und legt sie in der Settings-Tabelle ab.
    /// </summary>
    Task<AestheticModel?> GetModelAsync(CancellationToken ct = default);
}

/// <inheritdoc cref="IQualityAesthetics"/>
public sealed class QualityAesthetics : IQualityAesthetics
{
    /// <summary>Settings-Schlüssel des Zwischenspeichers (JSON: Version, zwei float32-Vektoren als Base64).</summary>
    public const string CacheKey = "quality.aestheticVectors";

    private readonly IMotifEncoderFactory _encoders;
    private readonly ILibraryContextFactory _factory;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private AestheticModel? _cached;
    private string? _failedVersion;

    public QualityAesthetics(IMotifEncoderFactory encoders, ILibraryContextFactory factory, ILogger<QualityAesthetics>? logger = null)
    {
        _encoders = encoders;
        _factory = factory;
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    public string EmbeddingModelVersion => _encoders.ModelVersion;

    /// <summary>Anzahl Rechnungen mit dem Sprachmodell (Tests).</summary>
    public int Encodings { get; private set; }

    public async Task<AestheticModel?> GetModelAsync(CancellationToken ct = default)
    {
        var modelVersion = _encoders.ModelVersion;
        var version = AestheticPrompts.VersionFor(modelVersion);
        if (Volatile.Read(ref _cached) is { } hit && hit.Version == version)
        {
            return hit;
        }

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cached is { } again && again.Version == version)
            {
                return again;
            }

            var model = await LoadCachedAsync(version, modelVersion, ct).ConfigureAwait(false);
            if (model is null && _encoders.IsAvailable && _failedVersion != version)
            {
                try
                {
                    model = await Task.Run(() => Encode(modelVersion), ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Sprachmodell defekt o. ä.: ohne Bildwirkung weiter, in dieser Sitzung kein neuer Versuch mit diesem Modell.
                    _failedVersion = version;
                    _logger.LogWarning(ex, "Foto-Güte: Text-Vektoren der Bildwirkung nicht berechenbar — Bildwirkung bleibt leer");
                    return null;
                }

                await SaveAsync(model, ct).ConfigureAwait(false);
                _logger.LogInformation("Foto-Güte: Text-Vektoren der Bildwirkung gerechnet ({Count} Beschreibungen)", AestheticPrompts.Positive.Count + AestheticPrompts.Negative.Count);
            }

            Volatile.Write(ref _cached, model);
            return model;
        }
        finally
        {
            _lock.Release();
        }
    }

    private AestheticModel Encode(string modelVersion)
    {
        using var encoder = _encoders.CreateTextEncoder();
        var texts = AestheticPrompts.Positive.Concat(AestheticPrompts.Negative).ToList();
        var vectors = encoder.EncodeTexts(texts);
        Encodings++;
        var positive = MotifVectors.MeanNormalized(vectors.Take(AestheticPrompts.Positive.Count).ToList());
        var negative = MotifVectors.MeanNormalized(vectors.Skip(AestheticPrompts.Positive.Count).ToList());
        return new AestheticModel(modelVersion, positive, negative);
    }

    private async Task<AestheticModel?> LoadCachedAsync(string version, string modelVersion, CancellationToken ct)
    {
        await using var ctx = _factory.Create();
        var text = await ctx.Settings.AsNoTracking().Where(s => s.Key == CacheKey).Select(s => s.Value).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        try
        {
            var stored = JsonSerializer.Deserialize<StoredVectors>(text);
            if (stored is null || stored.Version != version)
            {
                return null;
            }

            return new AestheticModel(
                modelVersion,
                MotifVectors.Decode(Convert.FromBase64String(stored.Positive)),
                MotifVectors.Decode(Convert.FromBase64String(stored.Negative)));
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException or InvalidDataException)
        {
            _logger.LogWarning(ex, "Foto-Güte: zwischengespeicherte Text-Vektoren unlesbar — werden neu gerechnet");
            return null;
        }
    }

    private async Task SaveAsync(AestheticModel model, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(new StoredVectors(
            model.Version,
            Convert.ToBase64String(MotifVectors.Encode(model.Positive)),
            Convert.ToBase64String(MotifVectors.Encode(model.Negative))));
        await using var ctx = _factory.Create();
        var row = await ctx.Settings.FindAsync([CacheKey], ct).ConfigureAwait(false);
        if (row is null)
        {
            ctx.Settings.Add(new Setting { Key = CacheKey, Value = json });
        }
        else
        {
            row.Value = json;
        }

        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private sealed record StoredVectors(string Version, string Positive, string Negative);
}
