using Microsoft.Extensions.Logging;
using PhotoViewerV3.Domain.Entities;

namespace PhotoViewerV3.Services.Faces.Assignment;

/// <summary>
/// Personen-Details und Beziehungen (0.2.47, ADR-0045) — im Zuordnungsdienst, damit derselbe Single-Writer
/// (<see cref="_writeLock"/>) und dieselbe <see cref="PersonsChangedMessage"/> gelten wie beim Benennen/Zusammenführen.
/// Kein Neu-Lernen nötig: Details ändern keine Zuordnung.
/// </summary>
public sealed partial class FaceRecognitionService : IPersonDetailsService
{
    public Task<PersonDetails?> GetPersonDetailsAsync(int personId, CancellationToken ct = default)
        => Task.Run(
            () =>
            {
                using var connection = _factory.OpenSqliteConnection();
                return PersonDetailsStore.ReadAll(connection, personId).FirstOrDefault();
            },
            ct);

    public Task<IReadOnlyList<PersonDetails>> GetAllPersonDetailsAsync(CancellationToken ct = default)
        => Task.Run<IReadOnlyList<PersonDetails>>(
            () =>
            {
                using var connection = _factory.OpenSqliteConnection();
                return PersonDetailsStore.ReadAll(connection);
            },
            ct);

    public async Task<int> CreatePersonAsync(PersonDetailsInput input, CancellationToken ct = default)
    {
        var normalized = Validate(input);
        var id = await WriteAsync(silent: false, (connection, tx) =>
        {
            if (FaceAssignmentStore.FindPersonByName(connection, tx, normalized.DisplayName) is { } existing)
            {
                throw new PersonNameTakenException(normalized.DisplayName, existing);
            }

            var relationId = PersonDetailsStore.ResolveRelation(connection, tx, normalized.Relation);
            return PersonDetailsStore.InsertPerson(connection, tx, normalized, relationId);
        }, ct).ConfigureAwait(false);
        _logger.LogInformation("Person angelegt (Id {PersonId}, mit Details)", id);
        await NotifyAsync(retrain: false, ct).ConfigureAwait(false);
        return id;
    }

    public async Task UpdatePersonDetailsAsync(int personId, PersonDetailsInput input, CancellationToken ct = default)
    {
        var normalized = Validate(input);
        await WriteAsync(silent: false, (connection, tx) =>
        {
            RequirePerson(connection, tx, personId);
            if (FaceAssignmentStore.FindPersonByName(connection, tx, normalized.DisplayName, personId) is { } existing)
            {
                throw new PersonNameTakenException(normalized.DisplayName, existing);
            }

            var relationId = PersonDetailsStore.ResolveRelation(connection, tx, normalized.Relation);
            return PersonDetailsStore.UpdatePerson(connection, tx, personId, normalized, relationId);
        }, ct).ConfigureAwait(false);
        _logger.LogInformation("Details der Person {PersonId} gespeichert", personId);
        await NotifyAsync(retrain: false, ct).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<PersonRelationInfo>> GetRelationsAsync(CancellationToken ct = default)
        => Task.Run<IReadOnlyList<PersonRelationInfo>>(
            () =>
            {
                using var connection = _factory.OpenSqliteConnection();
                return PersonDetailsStore.ReadRelations(connection);
            },
            ct);

    public async Task RenameRelationAsync(int relationId, string name, CancellationToken ct = default)
    {
        var cleaned = PersonNames.CleanOrNull(name) ?? throw new ArgumentException("Die Beziehung darf nicht leer sein.", nameof(name));
        await WriteAsync(silent: false, (connection, tx) =>
        {
            RequireRelation(connection, tx, relationId);
            if (PersonDetailsStore.FindRelation(connection, tx, cleaned, relationId) is { } existing)
            {
                throw new RelationNameTakenException(cleaned, existing);
            }

            return FaceAssignmentStore.Execute(connection, tx, "UPDATE PersonRelations SET Name = $n WHERE Id = $r;", ("$n", cleaned), ("$r", relationId));
        }, ct).ConfigureAwait(false);
        _logger.LogInformation("Beziehung {RelationId} umbenannt", relationId);
        await NotifyAsync(retrain: false, ct).ConfigureAwait(false);
    }

    public async Task MergeRelationsAsync(int sourceId, int targetId, CancellationToken ct = default)
    {
        if (sourceId == targetId)
        {
            throw new ArgumentException("Eine Beziehung kann nicht mit sich selbst zusammengeführt werden.", nameof(targetId));
        }

        var moved = await WriteAsync(silent: false, (connection, tx) =>
        {
            RequireRelation(connection, tx, sourceId);
            RequireRelation(connection, tx, targetId);
            var count = FaceAssignmentStore.Execute(connection, tx,
                "UPDATE Persons SET RelationId = $t WHERE RelationId = $s;", ("$s", sourceId), ("$t", targetId));
            FaceAssignmentStore.Execute(connection, tx, "DELETE FROM PersonRelations WHERE Id = $s;", ("$s", sourceId));
            return count;
        }, ct).ConfigureAwait(false);
        _logger.LogInformation("Beziehung {Source} in {Target} zusammengeführt ({Count} Personen)", sourceId, targetId, moved);
        await NotifyAsync(retrain: false, ct).ConfigureAwait(false);
    }

    public async Task DeleteRelationAsync(int relationId, CancellationToken ct = default)
    {
        await WriteAsync(silent: false, (connection, tx) =>
        {
            RequireRelation(connection, tx, relationId);
            // Explizit statt nur ON DELETE SET NULL: gilt auch, falls Fremdschlüssel einmal aus sind.
            FaceAssignmentStore.Execute(connection, tx, "UPDATE Persons SET RelationId = NULL WHERE RelationId = $r;", ("$r", relationId));
            return FaceAssignmentStore.Execute(connection, tx, "DELETE FROM PersonRelations WHERE Id = $r;", ("$r", relationId));
        }, ct).ConfigureAwait(false);
        _logger.LogInformation("Beziehung {RelationId} gelöscht", relationId);
        await NotifyAsync(retrain: false, ct).ConfigureAwait(false);
    }

    /// <summary>Normalisiert; leerer Anzeigename → <see cref="ArgumentException"/>.</summary>
    private static PersonDetailsInput Validate(PersonDetailsInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var normalized = input.Normalized();
        if (normalized.DisplayName.Length == 0)
        {
            throw new ArgumentException("Bitte einen Vornamen, Nachnamen oder Rufnamen angeben.", nameof(input));
        }

        return normalized;
    }

    private static void RequireRelation(Microsoft.Data.Sqlite.SqliteConnection connection, Microsoft.Data.Sqlite.SqliteTransaction tx, int relationId)
    {
        if (!PersonDetailsStore.RelationExists(connection, tx, relationId))
        {
            throw new InvalidOperationException("Die Beziehung gibt es nicht mehr.");
        }
    }

    /// <summary>
    /// Beim Zusammenführen zweier Personen (<see cref="MergePersonsAsync"/>): das Ziel behält seine Namen; fehlende
    /// Beziehung und fehlendes Geburtsdatum kommen von der Quelle, „Das bin ich“ wandert mit. Vor dem Löschen der Quelle.
    /// </summary>
    private static void MergeDetails(Microsoft.Data.Sqlite.SqliteConnection connection, Microsoft.Data.Sqlite.SqliteTransaction tx, int sourceId, int targetId)
    {
        var p = new (string, object)[] { ("$s", sourceId), ("$t", targetId) };
        FaceAssignmentStore.Execute(connection, tx, """
            UPDATE Persons SET
                RelationId = COALESCE(RelationId, (SELECT s.RelationId FROM Persons s WHERE s.Id = $s)),
                BirthMonth = CASE WHEN BirthYear IS NULL THEN (SELECT s.BirthMonth FROM Persons s WHERE s.Id = $s) ELSE BirthMonth END,
                BirthDay = CASE WHEN BirthYear IS NULL THEN (SELECT s.BirthDay FROM Persons s WHERE s.Id = $s) ELSE BirthDay END,
                BirthYear = COALESCE(BirthYear, (SELECT s.BirthYear FROM Persons s WHERE s.Id = $s))
            WHERE Id = $t;
            """, p);
        if (FaceAssignmentStore.Scalar(connection, tx, "SELECT IsMe FROM Persons WHERE Id = $s;", p) == 1)
        {
            FaceAssignmentStore.Execute(connection, tx, "UPDATE Persons SET IsMe = 0 WHERE Id = $s;", p);
            FaceAssignmentStore.Execute(connection, tx, "UPDATE Persons SET IsMe = 1 WHERE Id = $t;", p);
        }
    }
}
