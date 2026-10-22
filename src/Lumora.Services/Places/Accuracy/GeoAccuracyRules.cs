using Lumora.Domain.Enums;
using Lumora.Services.Metadata;

namespace Lumora.Services.Places.Accuracy;

/// <summary>
/// Regeln „Position geschätzt“ je Datei (ADR-0062, rein und getestet). Handys speichern bei einer Ortung über
/// Mobilfunk/WLAN nur Breite/Länge (Tags 1–4) ohne GPS-Höhe (6) und ohne GPS-Zeit (7/29) — echtes GPS liefert fast immer
/// beides mit. Zusätzlich verraten <c>GPSProcessingMethod</c> (27) und <c>GPSHPositioningError</c> (31) eine Schätzung.
/// Der unmögliche Sprung zu zeitnahen Fotos ist eine Regel über mehrere Dateien (<see cref="GeoJumpDetector"/>).
/// </summary>
public static class GeoAccuracyRules
{
    /// <summary>Fehlerradius, ab dem eine Position als geschätzt gilt (Meter). WLAN-Ortung liegt bei 20–200 m, Funkzellen bei km.</summary>
    public const double MaxHorizontalErrorMeters = 500;

    private static readonly string[] NetworkWords = ["NETWORK", "CELLID", "CELL", "WLAN", "WIFI", "WI-FI"];

    /// <summary>Bewertung aus den GPS-Angaben der Datei; ohne gültige Koordinate „Unbekannt“.</summary>
    public static (GeoAccuracy Accuracy, GeoAccuracyReason Reason) Classify(GpsExifDetails details)
    {
        if (!details.HasCoordinate)
        {
            return (GeoAccuracy.Unknown, GeoAccuracyReason.None);
        }

        var reason = GeoAccuracyReason.None;
        if (IsNetworkMethod(details.ProcessingMethod))
        {
            reason |= GeoAccuracyReason.NetworkMethod;
        }

        if (details.HorizontalError is > MaxHorizontalErrorMeters)
        {
            reason |= GeoAccuracyReason.LargePositionError;
        }

        if (!details.HasAltitude && !details.HasGpsTime)
        {
            reason |= GeoAccuracyReason.NoAltitudeNoTime;
        }

        return reason == GeoAccuracyReason.None ? (GeoAccuracy.Exact, reason) : (GeoAccuracy.Estimated, reason);
    }

    /// <summary>„NETWORK“, „CELLID“, „WLAN“, „WIFI“ (ohne Groß-/Kleinschreibung, auch als Teil, z. B. „network-fused“).</summary>
    public static bool IsNetworkMethod(string? method)
        => !string.IsNullOrWhiteSpace(method)
           && NetworkWords.Any(w => method.Contains(w, StringComparison.OrdinalIgnoreCase));

    /// <summary>Begründung in Worten (Liste, Lightbox): „ohne GPS-Höhe/-Zeit“, „über Mobilfunk/WLAN geortet“ …</summary>
    public static string Describe(GeoAccuracyReason reason)
    {
        var parts = new List<string>(3);
        if (reason.HasFlag(GeoAccuracyReason.NetworkMethod))
        {
            parts.Add("über Mobilfunk/WLAN geortet");
        }

        if (reason.HasFlag(GeoAccuracyReason.LargePositionError))
        {
            parts.Add("großer Fehlerradius");
        }

        if (reason.HasFlag(GeoAccuracyReason.NoAltitudeNoTime))
        {
            parts.Add("ohne GPS-Höhe/-Zeit");
        }

        if (reason.HasFlag(GeoAccuracyReason.ImpossibleJump))
        {
            parts.Add("unmöglicher Sprung zu Nachbarfotos");
        }

        return parts.Count == 0 ? "geschätzt" : string.Join(" · ", parts);
    }

    /// <summary>Ausführliche Erklärung für die Lightbox.</summary>
    public static string Explain(GeoAccuracyReason reason)
    {
        if (reason.HasFlag(GeoAccuracyReason.NetworkMethod))
        {
            return "Die Kamera nennt als Verfahren eine Ortung über Mobilfunk bzw. WLAN — solche Positionen liegen oft Kilometer daneben.";
        }

        if (reason.HasFlag(GeoAccuracyReason.LargePositionError))
        {
            return "Die Kamera gibt selbst einen Fehlerradius von über 500 m an.";
        }

        if (reason.HasFlag(GeoAccuracyReason.NoAltitudeNoTime))
        {
            return "Das Handy hat nur Breite/Länge gespeichert — ohne GPS-Höhe und -Zeit. Das deutet auf eine Schätzung über Mobilfunk/WLAN hin.";
        }

        return reason.HasFlag(GeoAccuracyReason.ImpossibleJump)
            ? "Die Position passt nicht zu zeitnahen Fotos mit echtem GPS (unmöglich schnelle Ortswechsel)."
            : "Die Position ist vermutlich geschätzt.";
    }
}
