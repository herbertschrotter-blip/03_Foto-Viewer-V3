using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PhotoViewerV3.Services.Database;
using PhotoViewerV3.Services.Messaging;
using PhotoViewerV3.Services.Infrastructure;

namespace PhotoViewerV3.Services.Library;

/// <summary>
/// <see cref="ILibraryScope"/> über rohe Verbindungen (ADR-0048). Liest bei jedem Aufruf frisch (Libraries ist klein);
/// schreibt nur, wenn eine Bibliothek noch keine Gruppe hat (kurze <c>BEGIN IMMEDIATE</c>-Transaktion mit Wiederholung,
/// ADR-0030) — danach ist der Lesepfad schreibfrei.
/// </summary>
public sealed class LibraryScope : ILibraryScope
{
    private readonly ILibraryContextFactory _factory;
    private readonly ICatalogContext? _catalog; // ADR-0049: Katalog der Sitzung (null = normaler Katalog)
    private readonly IMessenger _messenger;
    private readonly ILogger<LibraryScope> _logger;

    public LibraryScope(
        ILibraryContextFactory factory,
        ICatalogContext? catalog = null,
        IMessenger? messenger = null,
        ILogger<LibraryScope>? logger = null)
    {
        _factory = factory;
        _catalog = catalog;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
        _logger = logger ?? NullLogger<LibraryScope>.Instance;
    }

    public Task<LibraryScopeInfo> GetCurrentAsync(CancellationToken ct = default)
        => Task.Run(async () =>
        {
            using var connection = _factory.OpenSqliteConnection();
            var active = LibraryGroupStore.ReadActive(connection);
            if (active is null)
            {
                return LibraryScopeInfo.None;
            }

            var (libraryId, groupId) = active.Value;
            if (groupId == 0 || !LibraryGroupStore.GroupExists(connection, null, groupId))
            {
                groupId = await EnsureGroupAsync(connection, libraryId, ct).ConfigureAwait(false);
            }

            return Build(connection, libraryId, groupId, allowAllLinked: true);
        }, ct);

    public Task<LibraryScopeInfo> GetForLibraryAsync(int libraryId, CancellationToken ct = default)
        => Task.Run(async () =>
        {
            using var connection = _factory.OpenSqliteConnection();
            var groupId = LibraryGroupStore.ReadGroupOf(connection, null, libraryId);
            if (groupId is null)
            {
                return LibraryScopeInfo.None;
            }

            var group = groupId.Value;
            if (group == 0 || !LibraryGroupStore.GroupExists(connection, null, group))
            {
                group = await EnsureGroupAsync(connection, libraryId, ct).ConfigureAwait(false);
            }

            return Build(connection, libraryId, group, allowAllLinked: false);
        }, ct);

    public Task<IReadOnlyList<LibraryGroupInfo>> GetGroupsAsync(CancellationToken ct = default)
        => Task.Run<IReadOnlyList<LibraryGroupInfo>>(async () =>
        {
            using var connection = _factory.OpenSqliteConnection();
            var withoutGroup = new List<int>();
            using (var cmd = LibraryGroupStore.Command(connection, null, """
                SELECT l.Id FROM Libraries l
                WHERE l.GroupId = 0 OR NOT EXISTS (SELECT 1 FROM LibraryGroups g WHERE g.Id = l.GroupId)
                ORDER BY l.Id;
                """))
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    withoutGroup.Add(reader.GetInt32(0));
                }
            }

            foreach (var libraryId in withoutGroup)
            {
                await EnsureGroupAsync(connection, libraryId, ct).ConfigureAwait(false);
            }

            // Verwaiste Gruppen (Bibliothek entfernt, Aufräumen damals gescheitert) samt unsichtbarer Begriffe wegräumen.
            var orphans = new List<int>();
            using (var cmd = LibraryGroupStore.Command(connection, null,
                       "SELECT g.Id FROM LibraryGroups g WHERE NOT EXISTS (SELECT 1 FROM Libraries l WHERE l.GroupId = g.Id);"))
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    orphans.Add(reader.GetInt32(0));
                }
            }

            if (orphans.Count > 0)
            {
                using var tx = await _factory.BusyRetry.BeginTransactionAsync(connection, ct).ConfigureAwait(false);
                foreach (var orphan in orphans)
                {
                    LibraryGroupStore.DeleteGroupIfEmpty(connection, tx, orphan);
                }

                tx.Commit();
                _logger.LogInformation("{Count} verwaiste Bibliotheks-Gruppe(n) aufgeräumt", orphans.Count);
            }

            var groups = new List<LibraryGroupInfo>();
            using (var cmd = LibraryGroupStore.Command(connection, null, """
                SELECT g.Id, g.Name, l.Id FROM LibraryGroups g JOIN Libraries l ON l.GroupId = g.Id
                ORDER BY g.Id, l.Id;
                """))
            using (var reader = cmd.ExecuteReader())
            {
                int? currentId = null;
                string name = string.Empty;
                var ids = new List<int>();
                while (reader.Read())
                {
                    var id = reader.GetInt32(0);
                    if (currentId is { } c && c != id)
                    {
                        groups.Add(new LibraryGroupInfo(c, name, ids));
                        ids = [];
                    }

                    currentId = id;
                    name = reader.GetString(1);
                    ids.Add(reader.GetInt32(2));
                }

                if (currentId is { } last)
                {
                    groups.Add(new LibraryGroupInfo(last, name, ids));
                }
            }

            return groups;
        }, ct);

    public async Task ShowAllLinkedAsync(int groupId, CancellationToken ct = default)
    {
        if (_catalog?.IsHidden == true)
        {
            throw new LibraryLinkNotAllowedException("Eine versteckte Bibliothek wird nie gemeinsam mit einer anderen angezeigt.");
        }

        var activeId = await Task.Run(async () =>
        {
            using var connection = _factory.OpenSqliteConnection();
            using var tx = await _factory.BusyRetry.BeginTransactionAsync(connection, ct).ConfigureAwait(false);
            var libraries = LibraryGroupStore.ReadGroupLibraries(connection, tx, groupId);
            if (libraries.Count < 2)
            {
                throw new InvalidOperationException("Diese Bibliothek ist mit keiner anderen verknüpft.");
            }

            var active = LibraryGroupStore.ReadActive(connection, tx);
            var target = active is { } a && libraries.Contains(a.LibraryId)
                ? a.LibraryId
                : Convert.ToInt32(LibraryGroupStore.Scalar(connection, tx, $"""
                    SELECT Id FROM Libraries WHERE GroupId = $g
                    ORDER BY LastOpenedAt DESC, CreatedAt DESC, Id DESC LIMIT 1;
                    """, ("$g", groupId)));
            if (active?.LibraryId != target)
            {
                // Mindestens eine Sekunde nach dem jüngsten anderen (Sekunden-Auflösung, wie LibraryService.SetActiveAsync)
                LibraryGroupStore.Execute(connection, tx, """
                    UPDATE Libraries SET LastOpenedAt = MAX($t, COALESCE((SELECT MAX(o.LastOpenedAt) FROM Libraries o WHERE o.Id <> $id), 0) + 1)
                    WHERE Id = $id;
                    """, ("$t", DateTimeOffset.UtcNow.ToUnixTimeSeconds()), ("$id", target));
            }

            LibraryGroupStore.WriteViewAllLinked(connection, tx, groupId);
            tx.Commit();
            return target;
        }, ct).ConfigureAwait(false);

        _logger.LogInformation("Ansicht „Alle verknüpften“: Gruppe {GroupId}, aktive Bibliothek {LibraryId}", groupId, activeId);
        _messenger.Send(new ActiveLibraryChangedMessage(activeId));
    }

    public async Task RenameGroupAsync(int groupId, string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var trimmed = name.Trim();
        var libraries = await Task.Run(async () =>
        {
            using var connection = _factory.OpenSqliteConnection();
            using var tx = await _factory.BusyRetry.BeginTransactionAsync(connection, ct).ConfigureAwait(false);
            if (LibraryGroupStore.Execute(connection, tx, "UPDATE LibraryGroups SET Name = $n WHERE Id = $g;", ("$n", trimmed), ("$g", groupId)) == 0)
            {
                throw new InvalidOperationException("Die Gruppe gibt es nicht mehr.");
            }

            var ids = LibraryGroupStore.ReadGroupLibraries(connection, tx, groupId);
            tx.Commit();
            return ids;
        }, ct).ConfigureAwait(false);

        _logger.LogInformation("Gruppe {GroupId} umbenannt", groupId);
        _messenger.Send(new LibraryGroupsChangedMessage(libraries, TermsChanged: false));
        var current = await GetCurrentAsync(ct).ConfigureAwait(false);
        if (current.IsAllLinked && current.GroupId == groupId)
        {
            // Galerie, Ordnerbaum und Statusleiste zeigen den Gruppennamen („Alle verknüpften (Name)“) → neu laden
            _messenger.Send(new ActiveLibraryChangedMessage(current.LibraryId));
        }
    }

    private LibraryScopeInfo Build(SqliteConnection connection, int libraryId, int groupId, bool allowAllLinked)
    {
        var groupLibraries = LibraryGroupStore.ReadGroupLibraries(connection, null, groupId);
        if (!groupLibraries.Contains(libraryId))
        {
            groupLibraries.Add(libraryId); // Wettlauf mit Lösen/Verknüpfen: die Bibliothek selbst gehört immer dazu
            groupLibraries.Sort();
        }

        var name = LibraryGroupStore.ReadGroupName(connection, null, groupId);
        var allLinked = allowAllLinked
            && _catalog?.IsHidden != true
            && groupLibraries.Count > 1
            && LibraryGroupStore.ReadViewAllLinked(connection) == groupId;
        return new LibraryScopeInfo(
            libraryId,
            groupId,
            name,
            allLinked ? groupLibraries : [libraryId],
            groupLibraries,
            allLinked);
    }

    private async Task<int> EnsureGroupAsync(SqliteConnection connection, int libraryId, CancellationToken ct)
    {
        using var tx = await _factory.BusyRetry.BeginTransactionAsync(connection, ct).ConfigureAwait(false);
        var groupId = LibraryGroupStore.EnsureGroup(connection, tx, libraryId);
        tx.Commit();
        _logger.LogInformation("Bibliothek {LibraryId} hat eine eigene Gruppe bekommen ({GroupId})", libraryId, groupId);
        return groupId;
    }
}
