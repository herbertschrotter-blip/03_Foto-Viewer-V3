namespace Lumora.Services.Places.Accuracy;

/// <summary>Ein verortetes Foto für die Sprung-Prüfung.</summary>
/// <param name="Utc">Aufnahme als Unix-Sekunden (UTC).</param>
/// <param name="IsExact">Nach den Tag-Regeln genau (nur solche zählen als Nachbarn und werden geprüft).</param>
/// <param name="IsStrong">Mit GPS-Zeitstempel — wird nie selbst markiert und wiegt als Nachbar mehr.</param>
public readonly record struct JumpSample(int FileId, long Utc, double Latitude, double Longitude, bool IsExact, bool IsStrong);

/// <summary>
/// „Unmöglicher Sprung“ (ADR-0062, rein und getestet): ein nach den Tag-Regeln genaues Foto ohne GPS-Zeit gilt als
/// geschätzt, wenn es zu zeitnahen genauen Fotos (≤ <see cref="WindowSeconds"/>) nur mit mehr als
/// <see cref="MaxSpeedKmh"/> passt. Sind starke Nachbarn (mit GPS-Zeit) da, entscheiden nur sie (Mehrheit); sonst die
/// Mehrheit von mindestens zwei schwachen Nachbarn — zwei Fotos, die sich nur gegenseitig widersprechen, bleiben beide
/// unmarkiert. Kurze Abstände unter <see cref="MinJumpMeters"/> zählen nie (GPS-Rauschen, Zeitrundung).
/// </summary>
public static class GeoJumpDetector
{
    /// <summary>Zeitfenster der Nachbarn (30 min, wie bei den Vorschlägen).</summary>
    public const long WindowSeconds = 30 * 60;

    /// <summary>Schneller ist zu Fuß, mit Rad oder Auto im Alltag nicht möglich (Fernzüge/Flüge liegen weiter auseinander).</summary>
    public const double MaxSpeedKmh = 150;

    /// <summary>Mindestabstand für einen Sprung.</summary>
    public const double MinJumpMeters = 2000;

    /// <summary>Höchstens so viele Nachbarn je Seite (dichte Serien).</summary>
    public const int MaxNeighborsPerSide = 20;

    /// <summary>Kürzeste Zeit für die Geschwindigkeit (Uhrzeiten auf die Minute genau).</summary>
    public const long MinSeconds = 60;

    /// <summary>FileIds der als „unmöglicher Sprung“ markierten Fotos.</summary>
    public static HashSet<int> Detect(IReadOnlyList<JumpSample> samples)
    {
        var exact = samples.Where(s => s.IsExact).OrderBy(s => s.Utc).ThenBy(s => s.FileId).ToArray();
        var flagged = new HashSet<int>();
        for (var i = 0; i < exact.Length; i++)
        {
            var p = exact[i];
            if (p.IsStrong)
            {
                continue;
            }

            int strong = 0, strongConflicts = 0, weak = 0, weakConflicts = 0;
            void Visit(JumpSample q)
            {
                var conflict = IsConflict(p, q);
                if (q.IsStrong)
                {
                    strong++;
                    strongConflicts += conflict ? 1 : 0;
                }
                else
                {
                    weak++;
                    weakConflicts += conflict ? 1 : 0;
                }
            }

            for (int j = i - 1, n = 0; j >= 0 && n < MaxNeighborsPerSide && p.Utc - exact[j].Utc <= WindowSeconds; j--, n++)
            {
                Visit(exact[j]);
            }

            for (int j = i + 1, n = 0; j < exact.Length && n < MaxNeighborsPerSide && exact[j].Utc - p.Utc <= WindowSeconds; j++, n++)
            {
                Visit(exact[j]);
            }

            var jump = strong > 0
                ? strongConflicts * 2 > strong
                : weak >= 2 && weakConflicts * 2 > weak;
            if (jump)
            {
                flagged.Add(p.FileId);
            }
        }

        return flagged;
    }

    /// <summary>Widerspruch: weiter als <see cref="MinJumpMeters"/> und schneller als <see cref="MaxSpeedKmh"/>.</summary>
    public static bool IsConflict(JumpSample a, JumpSample b)
    {
        var meters = GeoMath.DistanceMeters(a.Latitude, a.Longitude, b.Latitude, b.Longitude);
        if (meters < MinJumpMeters)
        {
            return false;
        }

        var seconds = Math.Max(MinSeconds, Math.Abs(a.Utc - b.Utc));
        return GeoMath.SpeedKmh(meters, seconds) > MaxSpeedKmh;
    }
}
