using System.IO;
using Microsoft.Data.Sqlite;
using Lumora.Domain.Entities;
using Lumora.Domain.Enums;
using Lumora.Gallery.Data;
using Lumora.Wpf.Tests.ArchiveTarget;

namespace Lumora.Wpf.Tests.Duplicates;

/// <summary>
/// Galerie-Filter „Doppelte“ (ADR-0053): nur Dateien mit byte-gleichem Gegenstück im Bereich, UND mit den übrigen
/// Datei-Filtern, veraltete Prüfsummen zählen nicht, Gegenstücke in anderen Bibliotheken nur im Bereich „Alle verknüpften“.
/// </summary>
public sealed class GalleryDuplicateFilterTests : IAsyncLifetime
{
    private ArchiveTestDb _db = null!;
    private GalleryPageSource _source = null!;
    private int _otherLibrary;

    public async Task InitializeAsync()
    {
        _db = await ArchiveTestDb.CreateAsync();
        var archive = await _db.AddFolderAsync(_db.ArchiveRootId, _db.ArchiveDir);
        var inbox = await _db.AddFolderAsync(_db.InboxRootId, _db.InboxDir);
        var a = await _db.AddFileAsync(archive, "a.jpg");
        var b = await _db.AddFileAsync(inbox, "a-kopie.jpg");
        var c = await _db.AddFileAsync(archive, "c.jpg");
        var d = await _db.AddFileAsync(archive, "d-alt.jpg");
        var e = await _db.AddFileAsync(archive, "e.mp4", MediaType.Video);
        var e2 = await _db.AddFileAsync(inbox, "e-kopie.mp4", MediaType.Video);

        // zweite Bibliothek mit einer Kopie von c.jpg
        await using (var ctx = _db.Factory.Create())
        {
            var other = new Library { Name = "Zweite", RootPath = Path.Combine(_db.RootDir, "zweite"), CreatedAt = DateTime.UtcNow, LastOpenedAt = DateTime.UtcNow };
            ctx.Libraries.Add(other);
            await ctx.SaveChangesAsync();
            _otherLibrary = other.Id;
            var folder = new Folder { LibraryId = other.Id, Path = other.RootPath, Name = "zweite", ScannedAt = DateTime.UtcNow };
            ctx.Folders.Add(folder);
            await ctx.SaveChangesAsync();
            ctx.Files.Add(new MediaFile { LibraryId = other.Id, FolderId = folder.Id, Name = "c-woanders.jpg", Extension = "jpg", Size = 1000, CreatedAtUtc = DateTime.UtcNow, ModifiedAtUtc = DateTime.UtcNow, MediaType = MediaType.Image });
            await ctx.SaveChangesAsync();
        }

        using var connection = _db.Factory.OpenSqliteConnection();
        Hash(connection, a.Id, 1);
        Hash(connection, b.Id, 1);
        Hash(connection, c.Id, 2);
        Hash(connection, d.Id, 1, stale: true); // gleiche Prüfsumme, aber für eine alte Dateiversion
        Hash(connection, e.Id, 3);
        Hash(connection, e2.Id, 3);
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT Id FROM Files WHERE Name = 'c-woanders.jpg'";
            Hash(connection, Convert.ToInt32(cmd.ExecuteScalar()), 2);
        }

        _source = new GalleryPageSource(_db.Factory);
        _source.Open();
    }

    private static void Hash(SqliteConnection connection, int fileId, byte seed, bool stale = false)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO FileFingerprints(FileId, ContentHash, ContentSize, ContentModifiedAt)
            SELECT Id, $hash, Size, ModifiedAtUtc - $stale FROM Files WHERE Id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", fileId);
        cmd.Parameters.AddWithValue("$hash", Enumerable.Repeat(seed, 32).ToArray());
        cmd.Parameters.AddWithValue("$stale", stale ? 60 : 0);
        cmd.ExecuteNonQuery();
    }

    public Task DisposeAsync()
    {
        _source.Dispose();
        SqliteConnection.ClearAllPools();
        _db.Dispose();
        return Task.CompletedTask;
    }

    private async Task<List<string>> NamesAsync(GalleryQuery query)
    {
        var count = await _source.GetTotalCountAsync(query, CancellationToken.None);
        var rows = await _source.LoadPageAsync(query, 0, 100, count, CancellationToken.None);
        rows.Should().HaveCount(count, "Zähl- und Seiten-SQL nutzen dieselben Bedingungen");
        return rows.Select(r => r.Name).ToList();
    }

    [Fact]
    public async Task Nur_Dateien_mit_byte_gleichem_Gegenstueck_im_Bereich()
    {
        var filter = GalleryFilterState.None with { Duplicates = new GalleryDuplicateFilter([_db.LibraryId]) };
        filter.ActiveCount.Should().Be(1);
        filter.ToString().Should().EndWith(" Doppelte");

        (await NamesAsync(new GalleryQuery(_db.LibraryId, filter, null)))
            .Should().BeEquivalentTo("a.jpg", "a-kopie.jpg", "e.mp4", "e-kopie.mp4");

        var images = filter with { Media = GalleryMediaKind.Images };
        (await NamesAsync(new GalleryQuery(_db.LibraryId, images, null))).Should().BeEquivalentTo("a.jpg", "a-kopie.jpg");

        var inbox = filter with { Location = GalleryLocation.Inbox };
        (await NamesAsync(new GalleryQuery(_db.LibraryId, inbox, null))).Should().BeEquivalentTo("a-kopie.jpg", "e-kopie.mp4");
    }

    [Fact]
    public async Task Gegenstueck_in_anderer_Bibliothek_zaehlt_nur_in_Alle_verknuepften()
    {
        var linked = GalleryFilterState.None with { Duplicates = new GalleryDuplicateFilter([_db.LibraryId, _otherLibrary]) };
        var query = new GalleryQuery(_db.LibraryId, linked, null) { LibraryIds = [_db.LibraryId, _otherLibrary] };

        (await NamesAsync(query)).Should().BeEquivalentTo("a.jpg", "a-kopie.jpg", "c.jpg", "c-woanders.jpg", "e.mp4", "e-kopie.mp4");
        GalleryPageSource.BuildFilterClause(linked).Should().Contain("IN (" + _db.LibraryId + "," + _otherLibrary + ")");
    }
}
