using CommunityToolkit.Mvvm.Messaging;
using Lumora.Domain.Enums;
using Lumora.Services.Duplicates;
using Lumora.Services.Messaging;

namespace Lumora.Tests.Duplicates;

/// <summary>
/// „Übrige in den Papierkorb“ (ADR-0053): Angaben übertragen (Favorit, Bewertung, Tags, Kategorien, Personen), Papierkorb
/// über den Datei-Operationen-Weg mit Protokoll, Rückgängig mit derselben FileId und allen Angaben, Übertragung zurück.
/// </summary>
public sealed class DuplicateCleanupTests : IAsyncLifetime
{
    private DuplicateWorld _w = null!;
    private int _keep;
    private int _delete;
    private int _tag;
    private int _category;
    private int _value;
    private string _deletePath = null!;

    public async Task InitializeAsync()
    {
        _w = await DuplicateWorld.CreateAsync();
        _w.Write("2024/bild.jpg", DuplicateWorld.Content(3));
        _deletePath = _w.Write("handy/bild-kopie.jpg", DuplicateWorld.Content(3), inInbox: true);
        await _w.ScanAsync();
        await _w.Index.RunPassAsync();
        _keep = await _w.IdAsync("bild.jpg");
        _delete = await _w.IdAsync("bild-kopie.jpg");

        _w.Exec("INSERT INTO Tags(Name, Color, SortOrder, CreatedAtUtc, GroupId) VALUES ('Urlaub', '#FF0000', 0, 1, 0)");
        _tag = _w.Scalar<int>("SELECT Id FROM Tags WHERE Name = 'Urlaub'");
        _w.Exec("INSERT INTO Categories(Name, SortOrder, CreatedAtUtc, GroupId) VALUES ('Anlass', 0, 1, 0)");
        _category = _w.Scalar<int>("SELECT Id FROM Categories WHERE Name = 'Anlass'");
        _w.Exec("INSERT INTO CategoryValues(CategoryId, Name, SortOrder) VALUES ($c, 'Hochzeit', 0)", ("$c", _category));
        _value = _w.Scalar<int>("SELECT Id FROM CategoryValues WHERE Name = 'Hochzeit'");
        _w.Exec("INSERT INTO Persons(Name, CreatedAtUtc, IsHidden) VALUES ('Anna', 1, 0)");
    }

    public async Task DisposeAsync() => await _w.DisposeAsync();

    private int Anna => _w.Scalar<int>("SELECT Id FROM Persons WHERE Name = 'Anna'");

    /// <summary>Die gelöschte Datei trägt Favorit, 4 Sterne, Tag, Kategorie und Anna; die behaltene ist analysiert, aber ohne Person.</summary>
    private (int KeepFace, int DeleteFace) Decorate()
    {
        _w.Exec("UPDATE Files SET IsFavorite = 1, Rating = 4 WHERE Id = $id", ("$id", _delete));
        _w.Exec("INSERT INTO FileTags(FileId, TagId) VALUES ($f, $t)", ("$f", _delete), ("$t", _tag));
        _w.Exec("INSERT INTO FileCategoryValues(FileId, CategoryId, ValueId) VALUES ($f, $c, $v)", ("$f", _delete), ("$c", _category), ("$v", _value));
        var deleteFace = _w.InsertFace(_delete, 0.40, 0.30, 0.10, 0.12, Anna);
        _w.Exec("INSERT INTO FaceScans(FileId, ModelVersion, ScannedAtUtc, FaceCount, ErrorCode) VALUES ($f, 'v', 1, 1, NULL)", ("$f", _delete));
        var keepFace = _w.InsertFace(_keep, 0.41, 0.30, 0.10, 0.12, null, state: 0);
        _w.Exec("INSERT INTO FaceScans(FileId, ModelVersion, ScannedAtUtc, FaceCount, ErrorCode) VALUES ($f, 'v', 1, 1, NULL)", ("$f", _keep));
        return (keepFace, deleteFace);
    }

    [Fact]
    public async Task Aufraeumen_uebertraegt_Angaben_legt_in_den_Papierkorb_und_Rueckgaengig_holt_alles_zurueck()
    {
        var (keepFace, deleteFace) = Decorate();
        var removed = new List<FilesChangedMessage>();
        _w.Fx.Messenger.Register<FilesChangedMessage>(this, (_, m) => removed.Add(m));

        var result = await _w.Cleanup.CleanUpAsync([new DuplicateCleanupGroup(_keep, [_delete], IsExact: true)], transferMetadata: true);

        result.Recycled.Should().Be(1);
        result.Failed.Should().Be(0);
        result.Transferred.Should().Be(5, "Favorit, Bewertung, Tag, Kategorie, Person");
        result.PersonsNotTransferred.Should().Be(0);
        File.Exists(_deletePath).Should().BeFalse("im Papierkorb");
        _w.RecycleBin.Count.Should().Be(1);
        (await _w.Fx.FindFileByIdAsync(_delete)).Should().BeNull();
        var keep = (await _w.Fx.FindFileByIdAsync(_keep))!;
        keep.IsFavorite.Should().BeTrue();
        keep.Rating.Should().Be(4);
        _w.Ints("SELECT TagId FROM FileTags WHERE FileId = $f", ("$f", _keep)).Should().Equal(_tag);
        _w.Ints("SELECT ValueId FROM FileCategoryValues WHERE FileId = $f", ("$f", _keep)).Should().Equal(_value);
        _w.Scalar<int>("SELECT PersonId FROM Faces WHERE Id = $id", ("$id", keepFace)).Should().Be(Anna, "Gesicht mit der besten Überdeckung");
        _w.Scalar<int>("SELECT State FROM Faces WHERE Id = $id", ("$id", keepFace)).Should().Be(2);
        removed.Should().Contain(m => m.Kind == FilesChangeKind.Removed && m.FileIds.Contains(_delete));
        removed.Should().Contain(m => m.Kind == FilesChangeKind.Metadata && m.FileIds.Contains(_keep));

        // Protokoll (ADR-0012): Recycle-Batch mit Beschreibung und Rückgängig-Daten
        var batch = (await _w.Fx.GetBatchAsync(result.BatchId))!;
        batch.OperationType.Should().Be(OperationType.Recycle);
        batch.Status.Should().Be(BatchStatus.Applied);
        batch.Description.Should().Be("Doppelte aufgeräumt: 1 Datei(en) in den Papierkorb");
        (await _w.Fx.GetOperationsAsync(result.BatchId)).Should().ContainSingle().Which.SourcePath.Should().Be(_deletePath);
        var undo = await _w.Cleanup.GetUndoableAsync();
        undo.Should().NotBeNull();
        undo!.BatchId.Should().Be(result.BatchId);
        undo.FileCount.Should().Be(1);

        // Rückgängig: Datei zurück mit derselben FileId und allen Angaben, Übertragung an der behaltenen zurück
        var back = await _w.Cleanup.UndoAsync(result.BatchId);

        back.Should().NotBeNull();
        back!.Restored.Should().Be(1);
        File.Exists(_deletePath).Should().BeTrue();
        var restored = (await _w.Fx.FindFileByIdAsync(_delete))!;
        restored.Name.Should().Be("bild-kopie.jpg");
        restored.IsFavorite.Should().BeTrue();
        restored.Rating.Should().Be(4);
        _w.Ints("SELECT TagId FROM FileTags WHERE FileId = $f", ("$f", _delete)).Should().Equal(_tag);
        _w.Ints("SELECT ValueId FROM FileCategoryValues WHERE FileId = $f", ("$f", _delete)).Should().Equal(_value);
        _w.Scalar<int>("SELECT PersonId FROM Faces WHERE Id = $id AND FileId = $f", ("$id", deleteFace), ("$f", _delete)).Should().Be(Anna, "Gesicht mit derselben Id zurück");
        _w.Scalar<int>("SELECT COUNT(*) FROM FaceScans WHERE FileId = $f", ("$f", _delete)).Should().Be(1);
        _w.Fx.FtsRowIds("bild").Should().Contain(_delete);

        keep = (await _w.Fx.FindFileByIdAsync(_keep))!;
        keep.IsFavorite.Should().BeFalse();
        keep.Rating.Should().BeNull();
        _w.Ints("SELECT TagId FROM FileTags WHERE FileId = $f", ("$f", _keep)).Should().BeEmpty();
        _w.Ints("SELECT ValueId FROM FileCategoryValues WHERE FileId = $f", ("$f", _keep)).Should().BeEmpty();
        _w.Scalar<int?>("SELECT PersonId FROM Faces WHERE Id = $id", ("$id", keepFace)).Should().BeNull();
        (await _w.Fx.GetBatchAsync(result.BatchId))!.Status.Should().Be(BatchStatus.Undone);
        (await _w.Cleanup.GetUndoableAsync()).Should().BeNull();
        (await _w.Queries.GetGroupsAsync(new DuplicateQuery([_w.Library.Id], DuplicateMode.Exact))).Groups.Should().ContainSingle("Fingerabdruck kam mit zurück");
    }

    [Fact]
    public async Task Ohne_Uebertragen_bleibt_die_behaltene_Datei_unveraendert()
    {
        Decorate();
        var result = await _w.Cleanup.CleanUpAsync([new DuplicateCleanupGroup(_keep, [_delete], true)], transferMetadata: false);

        result.Recycled.Should().Be(1);
        result.Transferred.Should().Be(0);
        var keep = (await _w.Fx.FindFileByIdAsync(_keep))!;
        keep.IsFavorite.Should().BeFalse();
        _w.Ints("SELECT TagId FROM FileTags WHERE FileId = $f", ("$f", _keep)).Should().BeEmpty();
    }

    [Fact]
    public async Task Byte_gleich_und_behaltene_noch_nicht_analysiert_kopiert_die_Gesichter()
    {
        var deleteFace = _w.InsertFace(_delete, 0.2, 0.2, 0.1, 0.1, Anna);
        _w.InsertFace(_delete, 0.6, 0.2, 0.1, 0.1, null, state: 0);
        _w.Exec("INSERT INTO FaceScans(FileId, ModelVersion, ScannedAtUtc, FaceCount, ErrorCode) VALUES ($f, 'v', 1, 2, NULL)", ("$f", _delete));

        var result = await _w.Cleanup.CleanUpAsync([new DuplicateCleanupGroup(_keep, [_delete], true)], true);

        result.PersonsNotTransferred.Should().Be(0);
        _w.Scalar<int>("SELECT COUNT(*) FROM Faces WHERE FileId = $f", ("$f", _keep)).Should().Be(2, "beide Gesichter kopiert");
        _w.Scalar<int>("SELECT COUNT(*) FROM Faces WHERE FileId = $f AND PersonId = $p", ("$f", _keep), ("$p", Anna)).Should().Be(1);
        _w.Scalar<int>("SELECT COUNT(*) FROM FaceScans WHERE FileId = $f", ("$f", _keep)).Should().Be(1);

        await _w.Cleanup.UndoAsync(result.BatchId);
        _w.Scalar<int>("SELECT COUNT(*) FROM Faces WHERE FileId = $f", ("$f", _keep)).Should().Be(0, "Kopien wieder entfernt");
        _w.Scalar<int>("SELECT COUNT(*) FROM FaceScans WHERE FileId = $f", ("$f", _keep)).Should().Be(0);
        _w.Scalar<int>("SELECT FileId FROM Faces WHERE Id = $id", ("$id", deleteFace)).Should().Be(_delete);
    }

    [Fact]
    public async Task Aehnlich_ohne_passendes_Gesicht_zaehlt_als_nicht_uebertragbar()
    {
        _w.InsertFace(_delete, 0.2, 0.2, 0.1, 0.1, Anna);
        _w.Exec("INSERT INTO FaceScans(FileId, ModelVersion, ScannedAtUtc, FaceCount, ErrorCode) VALUES ($f, 'v', 1, 1, NULL)", ("$f", _keep));

        var result = await _w.Cleanup.CleanUpAsync([new DuplicateCleanupGroup(_keep, [_delete], IsExact: false)], true);

        result.PersonsNotTransferred.Should().Be(1, "die behaltene Datei hat dort kein Gesicht");
        result.Recycled.Should().Be(1);
    }

    [Fact]
    public async Task Gesperrte_Datei_bleibt_und_ihre_Uebertragung_wird_zurueckgenommen()
    {
        _w.Write("2024/dritte.jpg", DuplicateWorld.Content(3));
        await _w.RescanAsync();
        var third = await _w.IdAsync("dritte.jpg");
        _w.Exec("UPDATE Files SET Rating = 2 WHERE Id = $id", ("$id", third));
        _w.Exec("INSERT INTO FileTags(FileId, TagId) VALUES ($f, $t)", ("$f", _delete), ("$t", _tag));
        _w.RecycleBin.Locked.Add("bild-kopie.jpg");

        var result = await _w.Cleanup.CleanUpAsync([new DuplicateCleanupGroup(_keep, [_delete, third], true)], true);

        result.Recycled.Should().Be(1);
        result.Failed.Should().Be(1);
        result.Errors.Should().ContainSingle().Which.Should().Contain("gesperrt");
        File.Exists(_deletePath).Should().BeTrue("gesperrte Datei bleibt");
        _w.Ints("SELECT TagId FROM FileTags WHERE FileId = $f", ("$f", _keep)).Should().BeEmpty("Tag der gesperrten Datei zurückgenommen — er hängt ja weiter an ihr");
        (await _w.Fx.FindFileByIdAsync(_keep))!.Rating.Should().Be(2, "Bewertung der gelöschten dritten Datei übertragen");
        (await _w.Cleanup.GetUndoableAsync())!.FileCount.Should().Be(1, "Rückgängig-Daten nur für die gelöschte Datei");
    }

    [Fact]
    public async Task Teilweise_zurueckgeholt_laesst_sich_erneut_versuchen()
    {
        _w.Write("2024/dritte.jpg", DuplicateWorld.Content(3));
        await _w.RescanAsync();
        var third = await _w.IdAsync("dritte.jpg");
        var thirdPath = Path.Combine(_w.Fx.LibraryRoot, "2024", "dritte.jpg");
        var result = await _w.Cleanup.CleanUpAsync([new DuplicateCleanupGroup(_keep, [_delete, third], true)], true);
        File.WriteAllBytes(thirdPath, [1, 2, 3]); // Platz der dritten Datei belegt

        var first = await _w.Cleanup.UndoAsync(result.BatchId);
        first!.Restored.Should().Be(1);
        first.Failed.Should().Be(1);
        (await _w.Fx.GetBatchAsync(result.BatchId))!.Status.Should().Be(BatchStatus.Undone);
        (await _w.Cleanup.GetUndoableAsync())!.FileCount.Should().Be(1, "die dritte Datei ist noch offen");

        File.Delete(thirdPath);
        var second = await _w.Cleanup.UndoAsync(result.BatchId);
        second!.Restored.Should().Be(1);
        (await _w.Fx.FindFileByIdAsync(third)).Should().NotBeNull("mit derselben FileId zurück");
        (await _w.Cleanup.GetUndoableAsync()).Should().BeNull();
    }

    [Fact]
    public async Task Rueckgaengig_bringt_Gesicht_ohne_inzwischen_geloeschte_Person_zurueck()
    {
        var deleteFace = _w.InsertFace(_delete, 0.2, 0.2, 0.1, 0.1, Anna);
        var result = await _w.Cleanup.CleanUpAsync([new DuplicateCleanupGroup(_keep, [_delete], true)], transferMetadata: false);
        _w.Exec("DELETE FROM Persons WHERE Name = 'Anna'");

        await _w.Cleanup.UndoAsync(result.BatchId);

        _w.Scalar<int>("SELECT COUNT(*) FROM Faces WHERE Id = $id AND FileId = $f AND PersonId IS NULL", ("$id", deleteFace), ("$f", _delete))
            .Should().Be(1, "Gesicht kommt zurück, nur ohne die gelöschte Person");
    }

    [Fact]
    public async Task Behalten_und_loeschen_derselben_Datei_wird_abgelehnt()
    {
        var act = () => _w.Cleanup.CleanUpAsync([new DuplicateCleanupGroup(_keep, [_keep], true)], true);
        await act.Should().ThrowAsync<ArgumentException>();
        File.Exists(_deletePath).Should().BeTrue();
        (await _w.Cleanup.CleanUpAsync([], true)).BatchId.Should().Be(0);
    }

    [Fact]
    public async Task Rueckgaengig_scheitert_sauber_wenn_der_Platz_belegt_ist()
    {
        var result = await _w.Cleanup.CleanUpAsync([new DuplicateCleanupGroup(_keep, [_delete], true)], true);
        File.WriteAllBytes(_deletePath, [1, 2, 3]);

        var back = await _w.Cleanup.UndoAsync(result.BatchId);

        back!.Restored.Should().Be(0);
        back.Failed.Should().Be(1);
        back.Errors.Should().ContainSingle().Which.Should().Contain("belegt");
        (await _w.Fx.GetBatchAsync(result.BatchId))!.Status.Should().Be(BatchStatus.Applied, "nichts zurückgenommen — erneuter Versuch möglich");
    }
}
