using System;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.Data.Sqlite;

namespace PhotoViewerV3.Gallery.Data;

/// <summary>
/// Gemeinsamer Galerie-Filter für andere Seiten (ADR-0055): Die Karte („Orte“) liest ihre Dateien mit genau derselben
/// Klausel-Erzeugung wie Zählung und Seiten der Galerie — keine zweite Filter-SQL. Der Aufrufer bestimmt nur die Spalten und
/// hängt eigene Bedingungen an (z. B. „nur vorhandene Bilder und Videos“).
/// </summary>
public sealed partial class GalleryPageSource
{
    /// <summary>
    /// Befehl „SELECT <paramref name="columns"/> FROM … WHERE &lt;Bereich + alle Galerie-Filter + Ordner/Album&gt;
    /// <paramref name="extraConditions"/>“ auf der Verbindung des Aufrufers, Parameter gebunden.
    /// </summary>
    /// <param name="extraConditions">Zusätzliche Bedingungen („AND …“) und/oder „ORDER BY … LIMIT …“ (nur feste Texte, keine Benutzereingabe).</param>
    public static SqliteCommand CreateFilteredCommand(SqliteConnection connection, GalleryQuery query, string columns, string extraConditions)
    {
        var folder = ResolveFolderClause(connection, query);
        var count = BuildCountSql(query, folder.Clause).TrimEnd().TrimEnd(';');
        var cmd = connection.CreateCommand();
        cmd.CommandText = count.Replace("SELECT COUNT(*)", "SELECT " + columns, StringComparison.Ordinal) + "\n  " + extraConditions + ";";
        BindFilterParams(cmd, query, folder);
        return cmd;
    }

    /// <summary>Abfrage ohne die seitengebundenen Teile (Ordner samt Verknüpfungs-Schalter, Sortierung).</summary>
    public static GalleryQuery FilterOnly(GalleryQuery query)
        => query with { FolderId = null, IncludeSubfolders = true, Sort = GallerySort.CapturedDesc, CategorySort = null, IncludeFolderLinks = true };

    /// <summary>
    /// Eindeutiger Schlüssel des Filter-Teils einer Abfrage (SQL-Text und gebundene Werte, ohne Ordner und Sortierung):
    /// gleicher Schlüssel = gleiche Treffermenge. Braucht keine Verbindung.
    /// </summary>
    public static string FilterKey(GalleryQuery query)
    {
        var filterOnly = FilterOnly(query);
        var folder = new FolderClause(filterOnly.AlbumId is null ? string.Empty : AlbumClause, null, null, null);
        using var cmd = new SqliteCommand();
        BindFilterParams(cmd, filterOnly, folder);
        var sb = new StringBuilder(BuildCountSql(filterOnly, folder.Clause));
        foreach (var parameter in cmd.Parameters.Cast<SqliteParameter>().OrderBy(p => p.ParameterName, StringComparer.Ordinal))
        {
            sb.Append('|').Append(parameter.ParameterName).Append('=')
                .Append(Convert.ToString(parameter.Value, CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }
}
