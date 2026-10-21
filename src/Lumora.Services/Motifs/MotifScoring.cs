namespace Lumora.Services.Motifs;

/// <summary>
/// Wahrscheinlichkeit „Bild zeigt Begriff“ nach SigLIP (ADR-0052): σ(cos · e^logit_scale + logit_bias). SigLIP ist mit
/// einer Sigmoid-Verlustfunktion trainiert — jeder Begriff wird für sich bewertet (kein Softmax über die Liste), ein
/// Foto kann also viele oder keinen Treffer haben. Die Werte kommen aus dem Modell (Parameter <c>logit_scale</c> und
/// <c>logit_bias</c> von google/siglip2-base-patch16-224, gelesen aus model.safetensors am 2026-10-21).
/// </summary>
public sealed record MotifScoring(double LogitScale, double LogitBias)
{
    /// <summary>SigLIP 2 base-patch16-224: logit_scale = 4.724453 (e^… ≈ 112,7), logit_bias = −16.77172.</summary>
    public static MotifScoring Siglip2Base { get; } = new(4.724453, -16.77172);

    public double Temperature { get; } = Math.Exp(LogitScale);

    public float Probability(float cosine)
        => (float)(1.0 / (1.0 + Math.Exp(-(cosine * Temperature + LogitBias))));

    /// <summary>Kosinus, ab dem die Wahrscheinlichkeit <paramref name="probability"/> erreicht ist (Umkehrung).</summary>
    public float CosineFor(double probability)
    {
        var p = Math.Clamp(probability, 1e-6, 1 - 1e-6);
        return (float)((Math.Log(p / (1 - p)) - LogitBias) / Temperature);
    }
}
