using System.Globalization;
using Microsoft.Data.Sqlite;
using PhotoViewerV3.Domain.Enums;

namespace PhotoViewerV3.Services.Faces;

/// <summary>
/// Regel „Gesichter kleiner als … Pixel automatisch ignorieren“ (ADR-0040). Automatisch ignorierte Gesichter haben
/// <c>State = 3</c> wie manuell ignorierte — Lernen, Gruppen, Vorschläge, Galerie und Lightbox behandeln sie gleich —,
/// tragen aber die Markierung <see cref="AutoMarker"/> in <c>Faces.SuggestionScore</c> (bei Zustand 3 sonst immer NULL).
/// Daran erkennt die Regel ihre eigenen Entscheidungen: ein höherer/niedrigerer Wert oder „aus“ holt sie zurück,
/// manuell ignorierte und bestätigte Gesichter fasst die Regel nie an. Schwelle in Pixeln der Box-Seite
/// (<c>Faces.Size</c>, gemessen im analysierten Bild mit längster Kante 1280).
/// </summary>
public static class FaceAutoIgnore
{
    /// <summary>Settings-Schlüssel der Schwelle in Pixeln („0“ = aus).</summary>
    public const string MinSizeKey = "faces.autoIgnoreMinSize";

    /// <summary>Settings-Schlüssel: zuletzt auf den Bestand angewandte Schwelle (fehlt = noch nie angewandt).</summary>
    public const string AppliedKey = "faces.autoIgnoreMinSize.applied";

    /// <summary>
    /// Standard 24 px: Spike 09 misst p10 ≈ 25 px (p50 ≈ 62 px) bei 1280er-Kante — die Regel trifft etwa das kleinste
    /// Zehntel (Hintergrundgesichter). Unter 24 px trägt die Größe nichts mehr zur Qualität bei
    /// (<see cref="FaceQuality.MinUsefulSize"/>), ArcFace vergrößert solche Ausschnitte fast fünffach auf 112 px.
    /// </summary>
    public const int DefaultMinSize = 24;

    /// <summary>Höchste einstellbare Schwelle.</summary>
    public const int MaxMinSize = 400;

    /// <summary>Markierung in <c>Faces.SuggestionScore</c> bei <c>State = 3</c>: automatisch ignoriert (zu klein).</summary>
    public const float AutoMarker = -1f;

    /// <summary>Auswahl in den Einstellungen (0 = aus).</summary>
    public static IReadOnlyList<int> Choices { get; } = [0, 16, 20, 24, 32, 40, 48, 64];

    /// <summary>true = automatisch (wegen Größe) ignoriert, false = manuell ignoriert bzw. nicht ignoriert.</summary>
    public static bool IsAutomatic(FaceState state, float? suggestionScore)
        => state == FaceState.Ignored && suggestionScore is < 0f;

    /// <summary>Wird ein neues Gesicht dieser Größe bei der Schwelle automatisch ignoriert?</summary>
    public static bool Applies(int minSize, float size) => minSize > 0 && size < minSize;

    public static int Clamp(int minSize) => Math.Clamp(minSize, 0, MaxMinSize);

    /// <summary>Aktuelle Schwelle (Standard, wenn nicht gesetzt oder unlesbar).</summary>
    public static int ReadMinSize(SqliteConnection connection, SqliteTransaction? tx)
        => ReadInt(connection, tx, MinSizeKey) is { } value ? Clamp(value) : DefaultMinSize;

    /// <summary>Zuletzt auf den Bestand angewandte Schwelle; null = noch nie.</summary>
    public static int? ReadApplied(SqliteConnection connection, SqliteTransaction? tx) => ReadInt(connection, tx, AppliedKey);

    public static void WriteSetting(SqliteConnection connection, SqliteTransaction tx, string key, int value)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO Settings (Key, Value) VALUES ($k, $v) ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value.ToString(CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Wendet die Schwelle auf ALLE Gesichter an (alle Bibliotheken — die Einstellung ist global): automatisch ignorierte,
    /// die die Regel nicht mehr trifft, werden wieder unbestätigt (0); offene Gesichter (0/1) unter der Schwelle werden
    /// automatisch ignoriert. Bestätigte (2) und manuell ignorierte (3 ohne Markierung) bleiben. Merkt sich die
    /// angewandte Schwelle. In der Transaktion des Aufrufers.
    /// </summary>
    public static (List<long> Ignored, List<long> Restored) Apply(SqliteConnection connection, SqliteTransaction tx, int minSize)
    {
        minSize = Clamp(minSize);
        var restored = Ids(connection, tx, """
            UPDATE Faces SET State = 0, PersonId = NULL, SuggestedPersonId = NULL, SuggestionScore = NULL
            WHERE State = 3 AND SuggestionScore < 0 AND ($min <= 0 OR Size >= $min)
            RETURNING Id;
            """, minSize);
        var ignored = minSize <= 0
            ? []
            : Ids(connection, tx, """
                UPDATE Faces SET State = 3, PersonId = NULL, SuggestedPersonId = NULL, SuggestionScore = $marker
                WHERE State IN (0, 1) AND Size < $min
                RETURNING Id;
                """, minSize);
        WriteSetting(connection, tx, AppliedKey, minSize);
        return (ignored, restored);
    }

    private static List<long> Ids(SqliteConnection connection, SqliteTransaction tx, string sql, int minSize)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$min", minSize);
        cmd.Parameters.AddWithValue("$marker", (double)AutoMarker);
        var list = new List<long>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(reader.GetInt64(0));
        }

        return list;
    }

    private static int? ReadInt(SqliteConnection connection, SqliteTransaction? tx, string key)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT Value FROM Settings WHERE Key = $k;";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() is string text && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }
}
