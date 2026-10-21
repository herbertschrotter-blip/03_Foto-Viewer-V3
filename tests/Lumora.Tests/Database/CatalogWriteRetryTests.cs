using System.Diagnostics;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Services.Catalog;
using Lumora.Services.Categories;
using Lumora.Services.Database;
using Lumora.Services.Messaging;
using Lumora.Services.Tags;
using Lumora.Services.Watch;
using Lumora.Tests.Fixtures;

namespace Lumora.Tests.Database;

/// <summary>
/// „database is locked“ während „Neu aufbauen“ (CHANGELOG 0.2.24, Offen; ADR-0030): ein anderer Schreiber hält die
/// Schreibsperre länger als busy_timeout und CommandTimeout — hier verkürzt auf 100 ms bzw. 1 s statt 5 s/30 s, die
/// Sperre wird 2,5 s gehalten. Benutzeraktionen müssen danach ankommen, der Aufruf darf den Aufrufer nicht blockieren,
/// und eine Aktion auf eine Datei, deren FileId der Neuaufbau ersetzt, landet auf der neuen Id.
/// </summary>
public sealed class CatalogWriteRetryTests
{
    private static readonly TimeSpan Hold = TimeSpan.FromMilliseconds(2500);
    private static readonly TimeSpan Done = TimeSpan.FromSeconds(20);

    /// <summary>Factory gegen dieselbe DB wie die Fixture, mit verkürzten Zeiten (busy_timeout 100 ms, CommandTimeout 1 s).</summary>
    private static LibraryContextFactory ShortFactory(TempLibraryFixture fx, TimeSpan maxWait) => new(
        fx.Paths,
        new SqlitePragmaInterceptor(busyTimeoutMs: 100),
        busyRetry: new SqliteBusyRetryOptions { MaxWait = maxWait, RetryDelay = TimeSpan.FromMilliseconds(50) },
        messenger: fx.Messenger,
        defaultCommandTimeoutSeconds: 1);

    private static async Task<(int LibraryId, int AlphaId, int BravoId)> ArrangeAsync(TempLibraryFixture fx)
    {
        var library = await fx.CreateLibraryAsync();
        fx.WriteLibraryFile(Path.Combine("a", "alpha.jpg"));
        fx.WriteLibraryFile(Path.Combine("a", "bravo.jpg"));
        await fx.ScanAsync(library.Id);
        return (library.Id, (await fx.FindFileAsync("alpha.jpg"))!.Id, (await fx.FindFileAsync("bravo.jpg"))!.Id);
    }

    /// <summary>Hält BEGIN IMMEDIATE auf einer eigenen Verbindung — wie die Schreibtransaktion von „Neu aufbauen“.</summary>
    private sealed class WriteLockHolder : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly SqliteTransaction _transaction;

        public WriteLockHolder(ILibraryContextFactory factory)
        {
            _connection = factory.OpenSqliteConnection();
            _transaction = _connection.BeginTransaction();
        }

        public void Execute(string sql, params (string Name, object Value)[] parameters)
        {
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = _transaction;
            cmd.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                cmd.Parameters.AddWithValue(name, value);
            }

            cmd.ExecuteNonQuery();
        }

        /// <summary>Wie „Neu aufbauen“: die Datei bekommt eine neue FileId (gleicher Ordner, gleicher Name).</summary>
        public void ReplaceFileId(int oldId, int newId)
        {
            Execute("CREATE TEMP TABLE pv_test_copy AS SELECT * FROM Files WHERE Id = $old;", ("$old", oldId));
            Execute("DELETE FROM Files WHERE Id = $old;", ("$old", oldId));
            Execute("UPDATE temp.pv_test_copy SET Id = $new;", ("$new", newId));
            Execute("INSERT INTO Files SELECT * FROM temp.pv_test_copy;");
            Execute("DROP TABLE temp.pv_test_copy;");
        }

        public void Commit() => _transaction.Commit();

        public void Dispose()
        {
            _transaction.Dispose();
            _connection.Dispose();
        }
    }

    private sealed class WaitRecorder
    {
        private readonly List<int> _counts = new();

        public WaitRecorder(IMessenger messenger)
            => messenger.Register<WaitRecorder, CatalogWriteWaitMessage>(this, static (r, m) => { lock (r._counts) { r._counts.Add(m.WaitingWriters); } });

        public IReadOnlyList<int> Counts
        {
            get
            {
                lock (_counts)
                {
                    return _counts.ToList();
                }
            }
        }
    }

    /// <summary>Startet die Aktion, prüft, dass der Aufruf sofort zurückkehrt und wartet, gibt die Sperre frei, wartet aufs Ende.</summary>
    private static async Task<T> RunWhileLockedAsync<T>(WriteLockHolder holder, WaitRecorder waits, Func<Task<T>> action)
    {
        var clock = Stopwatch.StartNew();
        var task = action();
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1), "der Aufrufer (UI-Thread) darf nicht auf SQLite warten");

        await Task.Delay(Hold);
        task.IsCompleted.Should().BeFalse("die Sperre wird noch gehalten");
        waits.Counts.Should().Contain(1, "der wartende Schreiber meldet sich (Statusleiste)");

        holder.Commit();
        var result = await task.WaitAsync(Done);
        waits.Counts[^1].Should().Be(0, "nach dem Schreiben wartet niemand mehr");
        return result;
    }

    [Fact]
    public async Task Bewertung_waehrend_langer_Schreibtransaktion_kommt_nach_dem_Commit_an()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var (libraryId, alphaId, _) = await ArrangeAsync(fx);
        var waits = new WaitRecorder(fx.Messenger);
        using var svc = new CatalogUpdateService(ShortFactory(fx, Done), NullLogger<CatalogUpdateService>.Instance, fx.Messenger);
        using var holder = new WriteLockHolder(fx.ContextFactory);

        await RunWhileLockedAsync(holder, waits, async () => { await svc.SetRatingAsync([alphaId], 4); return 0; });

        (await fx.FindFileByIdAsync(alphaId))!.Rating.Should().Be(4);
        var message = fx.Messages.FilesChanged.Should().ContainSingle().Subject;
        message.LibraryId.Should().Be(libraryId);
        message.FileIds.Should().Equal(alphaId);
    }

    [Fact]
    public async Task Tag_Zuweisung_waehrend_langer_Schreibtransaktion_kommt_nach_dem_Commit_an()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var (_, alphaId, bravoId) = await ArrangeAsync(fx);
        using var setup = fx.CreateTagService();
        var tag = await setup.CreateAsync("Urlaub");
        var waits = new WaitRecorder(fx.Messenger);
        using var svc = new TagService(ShortFactory(fx, Done), NullLogger<TagService>.Instance, fx.Messenger);
        using var holder = new WriteLockHolder(fx.ContextFactory);

        var changed = await RunWhileLockedAsync(holder, waits, () => svc.AssignAsync([alphaId, bravoId], [tag.Id]));

        changed.Should().Be(2);
        var assigned = await setup.GetTagIdsForFilesAsync([alphaId, bravoId]);
        assigned[alphaId].Should().Equal(tag.Id);
        assigned[bravoId].Should().Equal(tag.Id);
    }

    [Fact]
    public async Task Kategorie_Wert_waehrend_langer_Schreibtransaktion_kommt_nach_dem_Commit_an()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var (_, alphaId, _) = await ArrangeAsync(fx);
        using var setup = fx.CreateCategoryService();
        var category = await setup.CreateAsync("XX", ["N"]);
        var waits = new WaitRecorder(fx.Messenger);
        using var svc = new CategoryService(ShortFactory(fx, Done), NullLogger<CategoryService>.Instance, fx.Messenger);
        using var holder = new WriteLockHolder(fx.ContextFactory);

        var changed = await RunWhileLockedAsync(holder, waits, () => svc.SetValueAsync([alphaId], category.Id, category.Values[0].Id));

        changed.Should().Be(1);
        (await setup.GetValuesForFilesAsync([alphaId]))[alphaId][category.Id].Should().Be(category.Values[0].Id);
    }

    [Fact]
    public async Task EF_Schreiber_warten_ueber_den_Interceptor_Einzelbefehl_Transaktion_und_Einstellung()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        using var setup = fx.CreateTagService();
        var first = await setup.CreateAsync("Eins");
        var second = await setup.CreateAsync("Zwei");
        var factory = ShortFactory(fx, Done);
        using var svc = new TagService(factory, NullLogger<TagService>.Instance, fx.Messenger);
        var settings = new WatchSettingsStore(factory);

        // SaveChanges mit einem INSERT (Autocommit, Reader mit RETURNING)
        using (var holder = new WriteLockHolder(fx.ContextFactory))
        {
            var created = await RunWhileLockedAsync(holder, new WaitRecorder(fx.Messenger), () => svc.CreateAsync("Drei"));
            created.Name.Should().Be("Drei");
        }

        // SaveChanges mit mehreren UPDATEs (EF beginnt eine Transaktion)
        using (var holder = new WriteLockHolder(fx.ContextFactory))
        {
            await RunWhileLockedAsync(holder, new WaitRecorder(fx.Messenger), async () => { await svc.ReorderAsync([second.Id, first.Id]); return 0; });
        }

        // Einstellung (Settings-Tabelle)
        using (var holder = new WriteLockHolder(fx.ContextFactory))
        {
            await RunWhileLockedAsync(holder, new WaitRecorder(fx.Messenger), async () =>
            {
                await settings.SaveAsync(new WatchSettings(false, TimeSpan.FromSeconds(7)));
                return 0;
            });
        }

        (await setup.GetAllAsync()).Select(t => t.Name).Should().Equal("Zwei", "Eins", "Drei");
        (await new WatchSettingsStore(fx.ContextFactory).LoadAsync()).Should().Be(new WatchSettings(false, TimeSpan.FromSeconds(7)));
    }

    [Fact]
    public async Task Bewertung_auf_eine_beim_Neuaufbau_ersetzte_FileId_landet_auf_der_neuen_Id()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var (libraryId, alphaId, bravoId) = await ArrangeAsync(fx);
        using var setup = fx.CreateTagService();
        var tag = await setup.CreateAsync("Urlaub");
        var factory = ShortFactory(fx, Done);
        using var catalog = new CatalogUpdateService(factory, NullLogger<CatalogUpdateService>.Instance, fx.Messenger);
        using var tags = new TagService(factory, NullLogger<TagService>.Instance, fx.Messenger);
        const int NewAlphaId = 10_000;

        using var holder = new WriteLockHolder(fx.ContextFactory);
        var rating = catalog.SetRatingAsync([alphaId, bravoId], 5);
        var tagging = tags.AssignAsync([alphaId], [tag.Id]);
        await Task.Delay(Hold);
        holder.ReplaceFileId(alphaId, NewAlphaId); // wie „Neu aufbauen“: neue FileId, gleicher Ordner + Name
        holder.Commit();
        await rating.WaitAsync(Done);
        (await tagging.WaitAsync(Done)).Should().Be(1);

        (await fx.FindFileByIdAsync(alphaId)).Should().BeNull();
        (await fx.FindFileByIdAsync(NewAlphaId))!.Rating.Should().Be(5, "die Bewertung darf nicht still verloren gehen");
        (await fx.FindFileByIdAsync(bravoId))!.Rating.Should().Be(5);
        (await setup.GetTagIdsForFilesAsync([NewAlphaId]))[NewAlphaId].Should().Equal(tag.Id);
        fx.Messages.FilesChanged.Should().OnlyContain(m => m.LibraryId == libraryId && !m.FileIds.Contains(alphaId));
        fx.Messages.FilesChanged.SelectMany(m => m.FileIds).Should().Contain(NewAlphaId);
    }

    [Fact]
    public async Task Ohne_Wiederholung_scheitert_die_Bewertung_wie_bisher_mit_database_is_locked()
    {
        // Gegenprobe zur Testanordnung: mit MaxWait = 0 verhält sich der Schreiber wie vor 0.2.29 (Ausnahme nach dem
        // ersten Versuch) — die Sperre provoziert also wirklich „database is locked“.
        using var fx = await TempLibraryFixture.CreateAsync();
        var (_, alphaId, _) = await ArrangeAsync(fx);
        using var svc = new CatalogUpdateService(ShortFactory(fx, TimeSpan.Zero), NullLogger<CatalogUpdateService>.Instance, fx.Messenger);
        using var holder = new WriteLockHolder(fx.ContextFactory);

        var act = () => svc.SetRatingAsync([alphaId], 4);

        (await act.Should().ThrowAsync<SqliteException>()).Which.SqliteErrorCode.Should().Be(5);
        holder.Commit();
        (await fx.FindFileByIdAsync(alphaId))!.Rating.Should().BeNull();
        fx.Messages.FilesChanged.Should().BeEmpty();
    }

    [Fact]
    public async Task Nach_der_Hoechstdauer_kommt_der_Fehler_beim_Aufrufer_an_statt_still_zu_verschwinden()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var (_, alphaId, _) = await ArrangeAsync(fx);
        var waits = new WaitRecorder(fx.Messenger);
        using var svc = new TagService(ShortFactory(fx, TimeSpan.FromSeconds(1.5)), NullLogger<TagService>.Instance, fx.Messenger);
        using var holder = new WriteLockHolder(fx.ContextFactory);

        var act = () => svc.RemoveForFilesAsync([alphaId]);

        await act.Should().ThrowAsync<SqliteException>();
        waits.Counts.Should().Equal(1, 0);
    }

    [Fact]
    public async Task Abbruch_beendet_das_Warten_ohne_zu_schreiben()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var (_, alphaId, _) = await ArrangeAsync(fx);
        using var svc = new CatalogUpdateService(ShortFactory(fx, Done), NullLogger<CatalogUpdateService>.Instance, fx.Messenger);
        using var holder = new WriteLockHolder(fx.ContextFactory);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));

        var act = () => svc.SetFavoriteAsync([alphaId], true, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        holder.Commit();
        (await fx.FindFileByIdAsync(alphaId))!.IsFavorite.Should().BeFalse();
        fx.Messages.FilesChanged.Should().BeEmpty();
    }

    [Fact]
    public void IsBusy_erkennt_Sperrfehler_auch_eingepackt()
    {
        var busy = new SqliteException("database is locked", 5);
        SqliteBusyRetry.IsBusy(busy).Should().BeTrue();
        SqliteBusyRetry.IsBusy(new DbUpdateException("x", busy)).Should().BeTrue();
        SqliteBusyRetry.IsBusy(new SqliteException("locked", 6)).Should().BeTrue();
        SqliteBusyRetry.IsBusy(new SqliteException("constraint", 19)).Should().BeFalse();
        SqliteBusyRetry.IsBusy(new InvalidOperationException()).Should().BeFalse();
    }
}
