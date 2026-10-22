using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Lumora.Services.Database;
using Lumora.Services.Library;
using Lumora.Services.Stories;

namespace Lumora.Services.Assistant;

/// <summary>Eine Treffer-Datei (Aufnahme-Reihenfolge).</summary>
public sealed record AssistantFile(int FileId, long Time, int Day, bool IsVideo);

/// <summary>
/// SQL des Assistenten (ADR-0063) — alles nur im aktuellen Bereich (<see cref="LibraryScopeInfo.LibraryIds"/>, ADR-0048):
/// Treffer eines Plans (Personen über bestätigte Gesichter, Begriffe über Tags/Kategorien/Motive/Orte, Reisen über die
/// Story-Erkennung, Medien, Bewertung), Zähler je Begriffs-Treffer, Orte des Bereichs und das erkannte Zuhause. Zeiträume
/// grob in SQL (Aufnahmezeit ± 2 Tage), genau über den Kalendertag in Ortszeit der Aufnahme (wie die Storys).
/// Ids stammen aus dem Katalog bzw. sind Zahlen — sicher einzubetten; Texte gehen nie in die SQL.
/// </summary>
public sealed class AssistantQueries(ILibraryContextFactory factory, StoryClock? clock = null)
{
    /// <summary>Halbe Kantenlänge des Suchrechtecks um einen Ort (km).</summary>
    public const double PlaceRadiusKm = 20;

    private const string Base = "f.Missing = 0 AND f.MediaType IN (0, 1)";
    private readonly StoryClock _clock = clock ?? new StoryClock();

    /// <summary>Anzahl Dateien eines Treffers im Bereich.</summary>
    public Task<int> CountAsync(LibraryScopeInfo scope, TermMatch match, CancellationToken ct = default)
        => Task.Run(() =>
        {
            using var connection = factory.OpenSqliteConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM Files f WHERE f.LibraryId IN ({scope.SqlLibraryIdList}) AND {Base} AND {Clause(match, scope.GroupId)};";
            return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }, ct);

    /// <summary>Orte (GeoNames-Ids), die im Bereich vorkommen.</summary>
    public Task<IReadOnlyList<int>> ReadPlaceIdsAsync(LibraryScopeInfo scope, CancellationToken ct = default)
        => Task.Run<IReadOnlyList<int>>(() =>
        {
            using var connection = factory.OpenSqliteConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT DISTINCT f.PlaceId FROM Files f WHERE f.LibraryId IN ({scope.SqlLibraryIdList}) AND f.PlaceId IS NOT NULL AND {Base};";
            var list = new List<int>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(reader.GetInt32(0));
            }

            return list;
        }, ct);

    /// <summary>Automatisch erkanntes Zuhause (häufigster Ort) für die Rückfrage „Wo ist Ihr Zuhause?“.</summary>
    public Task<StoryHome?> ReadDetectedHomeAsync(LibraryScopeInfo scope, CancellationToken ct = default)
        => Task.Run(() =>
        {
            using var connection = factory.OpenSqliteConnection();
            return StorySql.ReadHome(connection, scope.LibraryIds, StoryHomeSetting.Automatic);
        }, ct);

    /// <summary>Alle Treffer des Plans in Aufnahme-Reihenfolge.</summary>
    public Task<IReadOnlyList<AssistantFile>> ReadFilesAsync(LibraryScopeInfo scope, AssistantPlan plan, CancellationToken ct = default)
        => Task.Run<IReadOnlyList<AssistantFile>>(() =>
        {
            if (!scope.HasLibrary)
            {
                return [];
            }

            using var connection = factory.OpenSqliteConnection();
            return ReadFiles(connection, scope, plan, _clock.Zone, ct);
        }, ct);

    internal static List<AssistantFile> ReadFiles(SqliteConnection connection, LibraryScopeInfo scope, AssistantPlan plan, TimeZoneInfo zone, CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT f.Id, {StorySql.TimeKey}, f.CapturedAtOffset, f.MediaType FROM Files f WHERE {Where(scope, plan)};";
        var list = new List<AssistantFile>();
        using var reader = cmd.ExecuteReader();
        var n = 0;
        while (reader.Read())
        {
            if ((++n & 0xFFF) == 0)
            {
                ct.ThrowIfCancellationRequested();
            }

            var time = reader.GetInt64(1);
            int? offset = reader.IsDBNull(2) ? null : reader.GetInt32(2);
            var day = StorySql.LocalDay(time, offset, zone);
            if (plan.Time is { } t && !t.Contains(DateOnly.FromDayNumber(DateOnly.FromDateTime(DateTime.UnixEpoch).DayNumber + day)))
            {
                continue;
            }

            list.Add(new AssistantFile(reader.GetInt32(0), time, day, reader.GetInt32(3) == 1));
        }

        list.Sort(static (a, b) => a.Time != b.Time ? a.Time.CompareTo(b.Time) : a.FileId.CompareTo(b.FileId));
        return list;
    }

    /// <summary>WHERE-Teil des Plans (ohne genaue Tagesprüfung).</summary>
    internal static string Where(LibraryScopeInfo scope, AssistantPlan plan)
    {
        var sb = new StringBuilder();
        sb.Append($"f.LibraryId IN ({scope.SqlLibraryIdList}) AND {Base}");
        switch (plan.Media)
        {
            case IntentMedia.Photos: sb.Append(" AND f.MediaType = 0"); break;
            case IntentMedia.Videos: sb.Append(" AND f.MediaType = 1"); break;
        }

        if (plan.MinRating > 0)
        {
            sb.Append(" AND f.Rating >= ").Append(Inv(Math.Clamp(plan.MinRating, 1, 5)));
        }

        if (plan.FavoritesOnly)
        {
            sb.Append(" AND f.IsFavorite = 1");
        }

        if (plan.Persons.Count > 0)
        {
            var ids = AssistantStore.IdList(plan.Persons.Select(p => p.Id).Distinct());
            if (plan.PersonMatch == IntentMatch.Any)
            {
                sb.Append($" AND EXISTS (SELECT 1 FROM Faces fa WHERE fa.FileId = f.Id AND fa.State = 2 AND fa.PersonId IN ({ids}))");
            }
            else
            {
                foreach (var person in plan.Persons.Select(p => p.Id).Distinct())
                {
                    sb.Append($" AND EXISTS (SELECT 1 FROM Faces fa WHERE fa.FileId = f.Id AND fa.State = 2 AND fa.PersonId = {Inv(person)})");
                }
            }

            if (plan.Others == IntentOthers.None)
            {
                sb.Append($" AND NOT EXISTS (SELECT 1 FROM Faces fa WHERE fa.FileId = f.Id AND fa.State = 2 AND fa.PersonId IS NOT NULL AND fa.PersonId NOT IN ({ids}))");
            }
        }

        var terms = plan.Terms.Where(t => t.IsActive).Select(t => "(" + string.Join(" OR ", t.Matches.Where(m => m.Selected).Select(m => Clause(m, scope.GroupId))) + ")").ToList();
        if (terms.Count > 0)
        {
            sb.Append(" AND (").Append(string.Join(plan.TermMatch == IntentMatch.Any ? " OR " : " AND ", terms)).Append(')');
        }

        if (plan.Trip is { IsActive: true } trip)
        {
            var parts = new List<string>();
            if (trip.UseDetection)
            {
                parts.Add(Clause(new TermMatch(TermMatchKind.TripDetection, "Reise", [], 0), scope.GroupId));
            }

            parts.AddRange(trip.Extra.Where(m => m.Selected).Select(m => Clause(m, scope.GroupId)));
            sb.Append(" AND (").Append(string.Join(" OR ", parts)).Append(')');
        }

        if (plan.Time is { } time)
        {
            sb.Append(" AND f.CapturedAt IS NOT NULL");
            if (time.From is { } from)
            {
                sb.Append(" AND f.CapturedAt >= ").Append(Inv(Seconds(from) - (2 * 86400L)));
            }

            if (time.To is { } to)
            {
                sb.Append(" AND f.CapturedAt < ").Append(Inv(Seconds(to) + (3 * 86400L)));
            }
        }

        return sb.ToString();
    }

    /// <summary>Bedingung eines Treffers (eine Spalte von <c>Files f</c>).</summary>
    internal static string Clause(TermMatch match, int groupId)
    {
        var ids = AssistantStore.IdList(match.Ids);
        switch (match.Kind)
        {
            case TermMatchKind.Tag:
                return $"f.Id IN (SELECT ft.FileId FROM FileTags ft WHERE ft.TagId IN ({ids}))";
            case TermMatchKind.Category:
                return $"f.Id IN (SELECT fc.FileId FROM FileCategoryValues fc WHERE fc.CategoryId IN ({ids}))";
            case TermMatchKind.CategoryValue:
                return $"f.Id IN (SELECT fc.FileId FROM FileCategoryValues fc WHERE fc.ValueId IN ({ids}))";
            case TermMatchKind.Motif:
                return $"f.Id IN (SELECT fm.FileId FROM FileMotifs fm WHERE fm.TermId IN ({ids}))";
            case TermMatchKind.Person:
                return $"EXISTS (SELECT 1 FROM Faces fa WHERE fa.FileId = f.Id AND fa.State = 2 AND fa.PersonId IN ({ids}))";
            case TermMatchKind.Region:
            case TermMatchKind.Country:
                return $"f.PlaceId IN ({ids})";
            case TermMatchKind.Place when match.Latitude is { } lat && match.Longitude is { } lon:
                var dLat = match.RadiusKm / 110.574;
                var dLon = match.RadiusKm / (111.320 * Math.Max(0.05, Math.Cos(lat * Math.PI / 180)));
                return $"(f.Latitude BETWEEN {Inv(lat - dLat)} AND {Inv(lat + dLat)} AND f.Longitude BETWEEN {Inv(lon - dLon)} AND {Inv(lon + dLon)})";
            case TermMatchKind.TripDetection:
                return $"f.Id IN (SELECT si.FileId FROM StoryItems si CROSS JOIN Stories s ON s.Id = si.StoryId WHERE s.GroupId = {Inv(groupId)} AND s.Kind = 1 AND si.Origin >= 0)";
            default:
                return "0";
        }
    }

    /// <summary>Unix-Sekunden des Tagesbeginns (UTC).</summary>
    internal static long Seconds(DateOnly day) => new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeSeconds();

    private static string Inv(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Inv(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
