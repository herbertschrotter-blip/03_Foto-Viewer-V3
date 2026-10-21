using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace PhotoViewerV3.Gallery.Data;

/// <summary>
/// Serverseitige Sortierung nach dem Wert einer Kategorie, segmentweise (ADR-0031). Die Gesamtordnung
/// „Wert-Reihenfolge der Verwaltung, gleiche Werte nach Datum neu → alt und Id, ohne Wert zuletzt“ zerfällt in Segmente
/// je Wert: Eine Zählung liefert die Größe jedes Segments, die Seite liest dann nur aus dem Segment (bzw. den zwei
/// Segmenten), in das sie fällt — innerhalb eines Segments in der Reihenfolge des Index <c>IX_Files_Library_Modified</c>
/// mit <c>f.Id IN (Dateien des Werts)</c> (Bloom-Filter), samt Reverse-Trick vom Segmentende. Vor 0.2.30 lief eine
/// Sortierung über alle Dateien mit je einem Schlüssel-Lookup in <c>FileCategoryValues</c>, die bei 500 000 Dateien auf
/// jeder Seite mehrere Sekunden kostete.
/// </summary>
public sealed partial class GalleryPageSource
{
    /// <summary>
    /// Bis zu dieser Größe wird ein Wert-Segment ohne Suche und Filter von seinen Zuordnungen her gelesen (Index der
    /// Kategorie, dann Sortierung der wenigen Zeilen) statt über den Datums-Index der ganzen Bibliothek.
    /// </summary>
    private const int SmallSegmentSize = 4_000;

    /// <summary>Ein Segment der Kategorie-Sortierung: Dateien mit diesem Wert (<c>ValueId</c> null = ohne Wert).</summary>
    private readonly record struct CategorySegment(int? ValueId, long Count);

    /// <summary>Schlüssel (SQL + Parameter + <c>PRAGMA data_version</c>) und Inhalt der zuletzt gezählten Segmente.</summary>
    private string? _segmentCacheKey;
    private List<CategorySegment>? _segmentCache;

    /// <summary>Zuletzt von <see cref="GetTotalCountAsync"/> gezählte Gesamtzahl samt Schlüssel (wie oben).</summary>
    private string? _totalCacheKey;
    private long _totalCache;

    private IReadOnlyList<GalleryFileDto> LoadCategorySortedPage(
        SqliteConnection conn,
        GalleryQuery query,
        GalleryCategorySort sort,
        int pageIndex,
        int pageSize,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var folder = ResolveFolderClause(conn, query);

        // Eine Lese-Transaktion = ein WAL-Snapshot: Zählung und Seitenabfragen sehen denselben Stand, auch wenn eine
        // andere Verbindung zwischendurch committet (sonst passten Segmentgrößen und Zeilen nicht zusammen).
        using var tx = conn.BeginTransaction(deferred: true);
        var segments = LoadCategorySegments(conn, tx, query, folder, sort);
        var countMs = sw.Elapsed.TotalMilliseconds;

        var pageStart = (long)pageIndex * pageSize;
        var pageEnd = pageStart + pageSize;
        var rows = new List<GalleryFileDto>(pageSize);
        long segmentStart = 0;
        var segmentsRead = 0;
        foreach (var segment in segments)
        {
            var segmentEnd = segmentStart + segment.Count;
            if (segmentEnd > pageStart && segmentStart < pageEnd)
            {
                ct.ThrowIfCancellationRequested();
                var localOffset = Math.Max(pageStart, segmentStart) - segmentStart;
                var localLimit = Math.Min(pageEnd, segmentEnd) - segmentStart - localOffset;
                rows.AddRange(LoadCategorySegment(conn, tx, query, folder, sort, segment, localOffset, (int)localLimit, ct));
                segmentsRead++;
            }

            if (segmentEnd >= pageEnd)
            {
                break;
            }

            segmentStart = segmentEnd;
        }

        tx.Commit();
        sw.Stop();
        _logger.LogDebug(
            "Page-SQL Kategorie: Idx={Idx} Size={Size} Rows={Rows} Kategorie={Category}{Dir} Segmente={Segments}/{SegmentCount} Zählen={CountMs:N1} ms gesamt {Ms:N1} ms",
            pageIndex, pageSize, rows.Count, sort.CategoryId, sort.Descending ? " ↓" : " ↑", segmentsRead, segments.Count,
            countMs, sw.Elapsed.TotalMilliseconds);
        return rows;
    }

    /// <summary>
    /// Segmente in Anzeigereihenfolge: Werte nach SortOrder/Id (absteigend umgekehrt), danach „ohne Wert“ (Gesamtzahl
    /// minus Dateien mit Wert). Leere Segmente fallen weg. Die Zählung ist der teuerste Teil (500k: einige 100 ms) und
    /// wird beim Blättern wiederverwendet, solange Abfrage und Parameter gleich sind und keine andere Verbindung etwas in
    /// die DB geschrieben hat (<c>PRAGMA data_version</c> ändert sich bei jedem fremden Commit und bleibt innerhalb der
    /// Lese-Transaktion stabil; diese Verbindung schreibt nie). Läuft immer unter <c>_gate</c>.
    /// </summary>
    private List<CategorySegment> LoadCategorySegments(
        SqliteConnection conn, SqliteTransaction tx, GalleryQuery query, FolderClause folder, GalleryCategorySort sort)
    {
        // Snapshot mit einem ersten Lesezugriff festlegen, erst danach den Versionsstand lesen.
        var dataVersion = ReadDataVersion(conn, tx, startSnapshot: true);

        var values = new List<(int ValueId, int? SortOrder, long Count)>();
        string key;
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = BuildCategorySegmentCountSql(query, folder.Clause);
            BindFilterParams(cmd, query, folder);
            cmd.Parameters.AddWithValue("$sortCat", sort.CategoryId);
            key = BuildCacheKey(dataVersion, sort.Descending, cmd);
            if (key == _segmentCacheKey && _segmentCache is not null)
            {
                return _segmentCache;
            }

            _segmentCacheKey = null;
            _segmentCache = null;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                values.Add((reader.GetInt32(0), reader.IsDBNull(1) ? null : reader.GetInt32(1), reader.GetInt64(2)));
            }
        }

        // Werte nach SortOrder/Id; eine Zuordnung ohne Wert-Zeile (nur bei ausgeschalteten Fremdschlüsseln denkbar) steht
        // in beiden Richtungen hinter den Werten, direkt vor „ohne Wert“ (vor 0.2.30: mitten unter „ohne Wert“).
        values.Sort((a, b) =>
        {
            if (a.SortOrder is not { } aOrder || b.SortOrder is not { } bOrder)
            {
                var aMissing = a.SortOrder is null;
                var bMissing = b.SortOrder is null;
                return aMissing && bMissing ? a.ValueId.CompareTo(b.ValueId) : aMissing ? 1 : -1;
            }

            var order = aOrder != bOrder ? aOrder.CompareTo(bOrder) : a.ValueId.CompareTo(b.ValueId);
            return sort.Descending ? -order : order;
        });

        long total;
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = BuildCountSql(query, folder.Clause);
            BindFilterParams(cmd, query, folder);
            var totalKey = BuildCacheKey(dataVersion, descending: false, cmd);
            total = totalKey == _totalCacheKey ? _totalCache : Convert.ToInt64(cmd.ExecuteScalar());
        }

        var segments = new List<CategorySegment>(values.Count + 1);
        long withValue = 0;
        foreach (var (valueId, _, count) in values)
        {
            segments.Add(new CategorySegment(valueId, count));
            withValue += count;
        }

        if (total > withValue)
        {
            segments.Add(new CategorySegment(null, total - withValue));
        }

        _segmentCacheKey = key;
        _segmentCache = segments;
        return segments;
    }

    /// <summary>
    /// Merkt die Gesamtzahl aus <see cref="GetTotalCountAsync"/>, damit die erste Seite der Kategorie-Sortierung nicht
    /// noch einmal zählt. <paramref name="dataVersion"/> wird vor dem Zählen gelesen: ein Commit dazwischen macht den
    /// Eintrag beim nächsten Vergleich ungültig, nie umgekehrt.
    /// </summary>
    private void RememberTotal(long dataVersion, SqliteCommand countCommand, long total)
    {
        _totalCacheKey = BuildCacheKey(dataVersion, descending: false, countCommand);
        _totalCache = total;
    }

    /// <summary>Löscht die Zwischenspeicher (neue Verbindung: <c>data_version</c> beginnt dort von vorn).</summary>
    private void ResetCountCaches()
    {
        _segmentCacheKey = null;
        _segmentCache = null;
        _totalCacheKey = null;
        _totalCache = 0;
    }

    private static long ReadDataVersion(SqliteConnection conn, SqliteTransaction? tx, bool startSnapshot)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        if (startSnapshot)
        {
            cmd.CommandText = "SELECT COUNT(*) FROM Libraries;";
            cmd.ExecuteScalar();
        }

        cmd.CommandText = "PRAGMA data_version;";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static string BuildCacheKey(long dataVersion, bool descending, SqliteCommand command)
    {
        var sb = new StringBuilder();
        sb.Append(dataVersion).Append('|').Append(descending).Append('|').Append(command.CommandText);
        foreach (SqliteParameter parameter in command.Parameters)
        {
            sb.Append('|').Append(parameter.ParameterName).Append('=').Append(parameter.Value);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Liest einen Ausschnitt eines Segments. Liegt er näher am Segmentende, wird umgekehrt sortiert vom Ende gelesen und
    /// gedreht (Reverse-Trick wie bei <see cref="LoadPageAsync"/>).
    /// </summary>
    private static List<GalleryFileDto> LoadCategorySegment(
        SqliteConnection conn,
        SqliteTransaction tx,
        GalleryQuery query,
        FolderClause folder,
        GalleryCategorySort sort,
        CategorySegment segment,
        long offset,
        int limit,
        CancellationToken ct)
    {
        var offsetFromEnd = segment.Count - offset - limit;
        var reverse = offsetFromEnd < offset;
        var drive = segment.ValueId is null
            ? SegmentDrive.WithoutValue
            : IsUnfiltered(query) && segment.Count <= SmallSegmentSize ? SegmentDrive.SmallValue : SegmentDrive.Value;
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = BuildCategorySegmentPageSql(query, folder.Clause, drive, reverse);
        BindFilterParams(cmd, query, folder);
        cmd.Parameters.AddWithValue("$sortCat", sort.CategoryId);
        if (segment.ValueId is { } valueId)
        {
            cmd.Parameters.AddWithValue("$segValue", valueId);
        }

        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", reverse ? offsetFromEnd : offset);

        var rows = new List<GalleryFileDto>(limit);
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                rows.Add(ReadRow(reader));
            }
        }

        if (reverse)
        {
            rows.Reverse();
        }

        return rows;
    }

    /// <summary>Ohne Suche und ohne jeden Filter — dann entspricht die Segmentgröße der Zahl der Zuordnungen des Werts.</summary>
    private static bool IsUnfiltered(GalleryQuery query)
        => !query.HasSearch && !query.Filter.IsActive && !query.HasTagFilter && !query.HasFolder
           && query.ActiveCategoryFilters.Count == 0;

    private enum SegmentDrive
    {
        /// <summary>Datums-Index der Bibliothek, Zugehörigkeit per <c>f.Id IN (Dateien des Werts)</c>.</summary>
        Value,

        /// <summary>Zuordnungen des Werts aus dem Kategorie-Index, dann die wenigen Zeilen sortieren.</summary>
        SmallValue,

        /// <summary>Datums-Index der Bibliothek, Zugehörigkeit per <c>f.Id NOT IN (Dateien mit Wert)</c>.</summary>
        WithoutValue,
    }

    /// <summary>
    /// Dateien je Wert der Sortier-Kategorie unter allen Filtern, mit SortOrder des Werts. Ohne Suche treibt der Index
    /// <c>IX_FileCategoryValues_Category_Value_File</c> (Bereich der Kategorie, nach Wert und FileId geordnet — kein
    /// Sortieren fürs GROUP BY, Files-Lookups aufsteigend); mit Suche treiben die Treffer, je Treffer ein
    /// Schlüssel-Lookup.
    /// </summary>
    private static string BuildCategorySegmentCountSql(GalleryQuery query, string folderClause)
    {
        var source = query.HasSearch
            ? $"{BuildFilesSource(query)} JOIN FileCategoryValues scv ON scv.FileId = f.Id AND scv.CategoryId = $sortCat"
            : "FileCategoryValues scv CROSS JOIN Files f ON f.Id = scv.FileId";
        return $"""
            SELECT g.ValueId, sv.SortOrder, g.Cnt
            FROM (SELECT scv.ValueId AS ValueId, COUNT(*) AS Cnt
                  FROM {source}
                  WHERE scv.CategoryId = $sortCat
                    AND f.LibraryId = $libId
                    {BuildFilterClause(query.Filter)}
                    {BuildTagClause(query)}
                    {BuildCategoryClause(query)}
                    {BuildPersonClause(query)}
                    {folderClause}
                  GROUP BY scv.ValueId) g
            LEFT JOIN CategoryValues sv ON sv.Id = g.ValueId;
            """;
    }

    /// <summary>
    /// Ausschnitt eines Segments: innen nur Id und Datum, außen die Spalten für die wenigen Zeilen der Seite (verzögerter
    /// Join — übersprungene OFFSET-Zeilen kosten keine Tabellen-Lookups). Innen liefert der Index
    /// <c>IX_Files_Library_Modified</c> die Reihenfolge und <c>f.Id IN / NOT IN (…)</c> prüft die Zugehörigkeit über den
    /// Kategorie-Index; kleine Segmente ohne Filter lesen stattdessen ihre Zuordnungen und sortieren nur diese.
    /// </summary>
    private static string BuildCategorySegmentPageSql(GalleryQuery query, string folderClause, SegmentDrive drive, bool reverse)
    {
        var direction = reverse ? "ASC" : "DESC";
        var inner = drive switch
        {
            SegmentDrive.SmallValue => """
                FileCategoryValues c CROSS JOIN Files f ON f.Id = c.FileId
                WHERE c.CategoryId = $sortCat AND c.ValueId = $segValue AND f.LibraryId = $libId
                """,
            SegmentDrive.Value => $"""
                {BuildFilesSource(query)}
                WHERE f.LibraryId = $libId
                  AND f.Id IN (SELECT c.FileId FROM FileCategoryValues c WHERE c.CategoryId = $sortCat AND c.ValueId = $segValue)
                """,
            _ => $"""
                {BuildFilesSource(query)}
                WHERE f.LibraryId = $libId
                  AND f.Id NOT IN (SELECT c.FileId FROM FileCategoryValues c WHERE c.CategoryId = $sortCat)
                """,
        };
        return $"""
            SELECT {FileColumns}
            FROM (SELECT f.Id AS Id, f.ModifiedAtUtc AS Modified
                  FROM {inner}
                    {BuildFilterClause(query.Filter)}
                    {BuildTagClause(query)}
                    {BuildCategoryClause(query)}
                    {BuildPersonClause(query)}
                    {folderClause}
                  ORDER BY f.ModifiedAtUtc {direction}, f.Id {direction}
                  LIMIT $limit OFFSET $offset) p
            CROSS JOIN Files f ON f.Id = p.Id
            JOIN Folders fo ON fo.Id = f.FolderId
            {RootJoin}
            ORDER BY p.Modified {direction}, p.Id {direction};
            """;
    }

    /// <summary>
    /// Abfragepläne der Kategorie-Sortierung: Segment-Zählung, Wert-Segment, kleines Wert-Segment, Segment „ohne Wert“.
    /// </summary>
    private static IReadOnlyList<string> ExplainCategorySort(SqliteConnection conn, GalleryQuery query, GalleryCategorySort sort)
    {
        var folder = ResolveFolderClause(conn, query);
        var lines = new List<string>();
        foreach (var (sql, hasValue) in new[]
                 {
                     (BuildCategorySegmentCountSql(query, folder.Clause), false),
                     (BuildCategorySegmentPageSql(query, folder.Clause, SegmentDrive.Value, reverse: false), true),
                     (BuildCategorySegmentPageSql(query, folder.Clause, SegmentDrive.SmallValue, reverse: false), true),
                     (BuildCategorySegmentPageSql(query, folder.Clause, SegmentDrive.WithoutValue, reverse: false), false),
                 })
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "EXPLAIN QUERY PLAN " + sql;
            BindFilterParams(cmd, query, folder);
            cmd.Parameters.AddWithValue("$sortCat", sort.CategoryId);
            cmd.Parameters.AddWithValue("$limit", 100);
            cmd.Parameters.AddWithValue("$offset", 0);
            if (hasValue)
            {
                cmd.Parameters.AddWithValue("$segValue", 0);
            }

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                lines.Add(reader.GetString(3));
            }
        }

        return lines;
    }
}
