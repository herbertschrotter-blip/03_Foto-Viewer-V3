namespace PhotoViewerV3.Services.Scanning;

/// <summary>
/// Mass-Missing-Schwellwert aus PLAN Kap. 12: Fehlen nach einem Incremental-Scan mehr als 10.000 Dateien
/// ODER mehr als 20 % der bisher vorhandenen Katalogdateien, wird nichts automatisch als fehlend markiert —
/// die UI fragt nach („Ungewöhnlich viele Dateien fehlen“). Basis für die Prozentregel sind die Dateien der
/// Bibliothek, die vor dem Scan nicht bereits als Missing markiert waren.
/// </summary>
public static class MassMissingGuard
{
    public const int AbsoluteThreshold = 10_000;
    public const int PercentThreshold = 20;

    /// <param name="newlyMissing">Bisher vorhandene Dateien, die beim Scan nicht mehr auf der Platte lagen.</param>
    /// <param name="presentBefore">Katalogdateien der Bibliothek mit Missing = 0 vor dem Scan.</param>
    public static bool Exceeds(int newlyMissing, int presentBefore)
    {
        if (newlyMissing <= 0)
        {
            return false;
        }

        if (newlyMissing > AbsoluteThreshold)
        {
            return true;
        }

        return (long)newlyMissing * 100 > (long)presentBefore * PercentThreshold;
    }
}
