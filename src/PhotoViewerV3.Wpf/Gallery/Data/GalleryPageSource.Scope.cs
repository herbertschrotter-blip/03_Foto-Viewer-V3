using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PhotoViewerV3.Gallery.Data;

/// <summary>
/// Bibliotheks-Bereich der Galerie-SQL (ADR-0048). Eine Bibliothek: unverändert <c>LibraryId = $libId</c> (dieselben
/// Abfragepläne wie vor 0.2.51). Ansicht „Alle verknüpften“: <c>LibraryId IN (…)</c> über alle Bibliotheken der Gruppe —
/// die Ids sind Ganzzahlen aus dem Katalog und werden eingebettet. Alle Zähler, Seiten, Suche, Filter und Segmente der
/// Galerie nehmen ihre Bereichsbedingung von hier.
/// </summary>
public sealed partial class GalleryPageSource
{
    /// <summary>Bedingung „gehört zum Bereich der Abfrage“ für die Spalte (z. B. <c>f.LibraryId</c>, <c>d.LibraryId</c>).</summary>
    internal static string ScopeCondition(GalleryQuery query, string column)
        => ScopeCondition(query.ScopeLibraryIds, column);

    /// <summary>Wie oben für eine feste Liste (eine Bibliothek → <c>= $libId</c>, mehrere → <c>IN (…)</c>).</summary>
    internal static string ScopeCondition(IReadOnlyList<int> libraryIds, string column)
        => libraryIds.Count > 1
            ? $"{column} IN ({string.Join(",", libraryIds.Select(id => id.ToString(CultureInfo.InvariantCulture)))})"
            : $"{column} = $libId";

    /// <summary>Gruppe der ersten Bibliothek der Liste als SQL-Ausdruck (Begriffe der Gruppe: Personen, Beziehungen).</summary>
    internal static string GroupOfScopeSql(IReadOnlyList<int> libraryIds)
        => $"(SELECT GroupId FROM Libraries WHERE Id = {(libraryIds.Count > 0 ? libraryIds[0].ToString(CultureInfo.InvariantCulture) : "$libId")})";
}
