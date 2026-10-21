using System.Diagnostics;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Services.Database;
using Lumora.Services.Messaging;
using Lumora.Services.Scanning;

using Lumora.Services.Infrastructure;

namespace Lumora.Services.Library;

/// <summary>
/// <see cref="ILibraryLinkService"/>: Scan-Sperre aller betroffenen Bibliotheken (aufsteigend belegt — keine
/// Verklemmung), dann EINE Schreibtransaktion (<c>BEGIN IMMEDIATE</c> mit Wiederholung, ADR-0030) über
/// <see cref="LibraryLinkStore"/>; ein eigener Lock hält zwei Verknüpfungen auseinander.
/// </summary>
public sealed class LibraryLinkService : ILibraryLinkService, IDisposable
{
    private readonly ILibraryContextFactory _factory;
    private readonly ICatalogContext _catalog;
    private readonly IScanGate? _scanGate;
    private readonly IMessenger _messenger;
    private readonly ILogger<LibraryLinkService> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public LibraryLinkService(
        ILibraryContextFactory factory,
        ICatalogContext catalog,
        ILogger<LibraryLinkService>? logger = null,
        IMessenger? messenger = null,
        IScanGate? scanGate = null)
    {
        _factory = factory;
        _catalog = catalog;
        _scanGate = scanGate;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
        _logger = logger ?? NullLogger<LibraryLinkService>.Instance;
    }

    public bool IsLinkingAllowed => !_catalog.IsHidden;

    public Task<LibraryLinkPreview> PreviewLinkAsync(int libraryId, int otherLibraryId, CancellationToken ct = default)
    {
        EnsureAllowed();
        return Task.Run(async () =>
        {
            using var connection = _factory.OpenSqliteConnection();
            var (target, source) = await ResolveGroupsAsync(connection, libraryId, otherLibraryId, ct).ConfigureAwait(false);
            return LibraryLinkStore.PreviewLink(connection, libraryId, otherLibraryId, target, source);
        }, ct);
    }

    public async Task<LibraryLinkResult> LinkAsync(int libraryId, int otherLibraryId, IReadOnlyCollection<int>? keepApartSourcePersonIds = null, CancellationToken ct = default)
    {
        EnsureAllowed();
        var keepApart = keepApartSourcePersonIds ?? [];
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            List<int> affected;
            using (var connection = _factory.OpenSqliteConnection())
            {
                var (target, source) = await ResolveGroupsAsync(connection, libraryId, otherLibraryId, ct).ConfigureAwait(false);
                affected = [.. LibraryGroupStore.ReadGroupLibraries(connection, null, target), .. LibraryGroupStore.ReadGroupLibraries(connection, null, source)];
            }

            var sw = Stopwatch.StartNew();
            LibraryLinkResult result;
            using (await AcquireGatesAsync(affected, ct).ConfigureAwait(false))
            {
                result = await WriteAsync((connection, tx) =>
                {
                    // In der Transaktion neu lesen: zwischen Vorschau und Schreiben kann sich etwas geändert haben.
                    var target = LibraryGroupStore.EnsureGroup(connection, tx, libraryId);
                    var source = LibraryGroupStore.EnsureGroup(connection, tx, otherLibraryId);
                    if (target == 0 || source == 0)
                    {
                        throw new InvalidOperationException("Eine der Bibliotheken gibt es nicht mehr.");
                    }

                    if (target == source)
                    {
                        throw new InvalidOperationException("Die Bibliotheken sind bereits verknüpft.");
                    }

                    var sourceName = LibraryGroupStore.ReadLibraryName(connection, tx, otherLibraryId);
                    return LibraryLinkStore.Link(connection, tx, target, source, sourceName, keepApart);
                }, ct).ConfigureAwait(false);
            }

            _logger.LogInformation(
                "Bibliotheken verknüpft: {Library} + {Other} → Gruppe {Group} — zusammengeführt: {Tags} Tags, {Categories} Kategorien, {Relations} Beziehungen, {Persons} Personen, {Motifs} Motiv-Begriffe; getrennt gehalten: {Apart} ({Ms:N0} ms)",
                libraryId, otherLibraryId, result.GroupId, result.TagsMerged, result.CategoriesMerged, result.RelationsMerged, result.PersonsMerged, result.MotifTermsMerged, result.PersonsKeptApart, sw.Elapsed.TotalMilliseconds);
            await NotifyAsync(affected, ct).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public Task<LibraryUnlinkPreview> PreviewUnlinkAsync(int libraryId, CancellationToken ct = default)
    {
        EnsureAllowed();
        return Task.Run(() =>
        {
            using var connection = _factory.OpenSqliteConnection();
            var group = LibraryGroupStore.ReadGroupOf(connection, null, libraryId)
                ?? throw new InvalidOperationException("Die Bibliothek gibt es nicht mehr.");
            return LibraryLinkStore.PreviewUnlink(connection, libraryId, group);
        }, ct);
    }

    public async Task<LibraryUnlinkResult> UnlinkAsync(int libraryId, CancellationToken ct = default)
    {
        EnsureAllowed();
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            List<int> affected;
            using (var connection = _factory.OpenSqliteConnection())
            {
                var group = LibraryGroupStore.ReadGroupOf(connection, null, libraryId)
                    ?? throw new InvalidOperationException("Die Bibliothek gibt es nicht mehr.");
                affected = LibraryGroupStore.ReadGroupLibraries(connection, null, group);
            }

            var sw = Stopwatch.StartNew();
            LibraryUnlinkResult result;
            using (await AcquireGatesAsync(affected, ct).ConfigureAwait(false))
            {
                result = await WriteAsync((connection, tx) =>
                {
                    var group = LibraryGroupStore.EnsureGroup(connection, tx, libraryId);
                    if (LibraryGroupStore.ReadGroupLibraries(connection, tx, group).Count < 2)
                    {
                        throw new InvalidOperationException("Die Bibliothek ist mit keiner anderen verknüpft.");
                    }

                    var newGroup = LibraryGroupStore.CreateGroup(connection, tx, LibraryGroupStore.ReadLibraryName(connection, tx, libraryId));
                    LibraryGroupStore.Execute(connection, tx, "UPDATE Libraries SET GroupId = $g WHERE Id = $id;", ("$g", newGroup), ("$id", libraryId));
                    return LibraryLinkStore.CopyTermsForLibrary(connection, tx, group, newGroup, libraryId);
                }, ct).ConfigureAwait(false);
            }

            _logger.LogInformation(
                "Verknüpfung gelöst: Bibliothek {Library} → eigene Gruppe {Group} — kopiert: {Tags} Tags, {Categories} Kategorien, {Relations} Beziehungen, {Persons} Personen, {Motifs} Motiv-Begriffe ({Ms:N0} ms)",
                libraryId, result.NewGroupId, result.TagsCopied, result.CategoriesCopied, result.RelationsCopied, result.PersonsCopied, result.MotifTermsCopied, sw.Elapsed.TotalMilliseconds);
            await NotifyAsync(affected, ct).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void EnsureAllowed()
    {
        if (_catalog.IsHidden)
        {
            throw new LibraryLinkNotAllowedException("Eine versteckte Bibliothek kann nicht mit anderen verknüpft werden.");
        }
    }

    private async Task<(int Target, int Source)> ResolveGroupsAsync(SqliteConnection connection, int libraryId, int otherLibraryId, CancellationToken ct)
    {
        if (libraryId == otherLibraryId)
        {
            throw new InvalidOperationException("Eine Bibliothek kann nicht mit sich selbst verknüpft werden.");
        }

        using var tx = await _factory.BusyRetry.BeginTransactionAsync(connection, ct).ConfigureAwait(false);
        var target = LibraryGroupStore.EnsureGroup(connection, tx, libraryId);
        var source = LibraryGroupStore.EnsureGroup(connection, tx, otherLibraryId);
        tx.Commit();
        if (target == 0 || source == 0)
        {
            throw new InvalidOperationException("Eine der Bibliotheken gibt es nicht mehr.");
        }

        if (target == source)
        {
            throw new InvalidOperationException("Die Bibliotheken sind bereits verknüpft.");
        }

        return (target, source);
    }

    private Task<T> WriteAsync<T>(Func<SqliteConnection, SqliteTransaction, T> write, CancellationToken ct)
        => Task.Run(async () =>
        {
            using var connection = _factory.OpenSqliteConnection();
            using var tx = await _factory.BusyRetry.BeginTransactionAsync(connection, ct).ConfigureAwait(false);
            var result = write(connection, tx);
            tx.Commit();
            return result;
        }, ct);

    /// <summary>Scan-Sperre aller Bibliotheken (aufsteigend, damit sich zwei Aufrufer nicht gegenseitig blockieren).</summary>
    private async Task<IDisposable> AcquireGatesAsync(IEnumerable<int> libraryIds, CancellationToken ct)
    {
        var held = new List<IDisposable>();
        if (_scanGate is null)
        {
            return new Releaser(held);
        }

        try
        {
            foreach (var id in libraryIds.Distinct().Order())
            {
                held.Add(await _scanGate.AcquireForFileOperationAsync(id, ct).ConfigureAwait(false));
            }
        }
        catch
        {
            new Releaser(held).Dispose();
            throw;
        }

        return new Releaser(held);
    }

    /// <summary>Nach dem Commit (Abbruch nicht mehr möglich): Umschalter/Einstellungen und alles mit Begriffen lädt neu.</summary>
    private async Task NotifyAsync(IReadOnlyList<int> affected, CancellationToken ct)
    {
        _ = ct;
        try
        {
            var active = await Task.Run(() =>
            {
                using var connection = _factory.OpenSqliteConnection();
                return LibraryGroupStore.ReadActive(connection)?.LibraryId;
            }, CancellationToken.None).ConfigureAwait(false);

            _messenger.Send(new LibraryGroupsChangedMessage(affected));
            if (active is { } id)
            {
                _messenger.Send(new ActiveLibraryChangedMessage(id)); // Galerie, Ordner, Personen, Filter laden neu
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Empfänger nach dem Verknüpfen warf eine Ausnahme");
        }
    }

    public void Dispose() => _writeLock.Dispose();

    private sealed class Releaser(List<IDisposable> held) : IDisposable
    {
        public void Dispose()
        {
            for (var i = held.Count - 1; i >= 0; i--)
            {
                held[i].Dispose();
            }

            held.Clear();
        }
    }
}
