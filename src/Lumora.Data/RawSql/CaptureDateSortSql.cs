namespace Lumora.Data.RawSql;

/// <summary>
/// Sortierung nach Aufnahmedatum (ADR-0046): Schlüssel <c>COALESCE(CapturedAt, ModifiedAtUtc)</c> — Dateien ohne
/// Aufnahmedatum stehen mit ihrem Änderungsdatum dazwischen (beides Unix-Sekunden UTC). Der Ausdrucks-Index
/// <see cref="IndexName"/> liefert die Reihenfolge je Bibliothek wie <c>IX_Files_Library_Modified</c> beim
/// Änderungsdatum. Der ORDER-BY-Ausdruck muss wörtlich dem Index-Ausdruck entsprechen, sonst nutzt SQLite den Index nicht.
/// </summary>
public static class CaptureDateSortSql
{
    public const string IndexName = "IX_Files_Library_Captured";

    /// <summary>Sortierschlüssel mit Tabellen-Alias <c>f</c> (Galerie-SQL).</summary>
    public const string SortKey = "COALESCE(f.CapturedAt, f.ModifiedAtUtc)";

    /// <summary>
    /// Legt den Index an (Migration AddCapturedAt; nach einem späteren Tabellen-Neuaufbau erneut ausführen). Mit Id als
    /// letzter Spalte deckt er auch die stabile Zweitsortierung ab — ohne sie sortiert SQLite gleiche Zeitpunkte in einem
    /// temporären B-Baum nach („USE TEMP B-TREE FOR LAST TERM OF ORDER BY“).
    /// </summary>
    public const string CreateIndexSql =
        "CREATE INDEX IF NOT EXISTS " + IndexName + " ON Files (LibraryId, COALESCE(CapturedAt, ModifiedAtUtc) DESC, Id DESC);";

    public const string DropIndexSql = "DROP INDEX IF EXISTS " + IndexName + ";";
}
