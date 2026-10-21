using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using PhotoViewerV3.Services.Faces.Crops;
using PhotoViewerV3.Services.Faces.Indexing;

namespace PhotoViewerV3.Tests.Faces.Assignment;

/// <summary>
/// Cache der Gesichts-Ausschnitte (ADR-0037): Pfad je Gesicht im Thumbnail-Ordner, je Quelldatei ein Render-Aufruf,
/// vorhandene Ausschnitte ohne erneutes Rendern, verworfene Ausschnitte bei geänderten Dateien, verwaiste werden
/// weggeräumt, „alles löschen“.
/// </summary>
public sealed class FaceCropServiceTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task Fordert_an_rendert_je_Datei_einmal_und_meldet_fertige_Ausschnitte()
    {
        using var world = await FaceAssignmentWorld.CreateAsync();
        var files = await world.CreateFilesAsync(2);
        var a1 = world.InsertFace(files[0], world.Embedding("a"), boxX: 0.1f);
        var a2 = world.InsertFace(files[0], world.Embedding("b"), boxX: 0.6f);
        var b1 = world.InsertFace(files[1], world.Embedding("a"));
        var renderer = new FakeCropRenderer();
        using var service = new FaceCropService(world.Fx.ContextFactory, world.Fx.Paths, renderer, NullLogger<FaceCropService>.Instance, world.Fx.Messenger, TimeSpan.FromMilliseconds(50));
        var ready = new List<FaceCropReadyEventArgs>();
        service.CropReady += e => { lock (ready) { ready.Add(e); } };
        service.Start();

        service.GetCropPath(world.LibraryId, a1).Should().Be(Path.Combine(world.Fx.Paths.GetLibraryThumbnailsPath(world.LibraryId), "faces", $"{a1}.jpg"));
        service.Request(world.LibraryId, [a1, a2, b1]);
        await WaitUntilAsync(() => Count(ready) == 3);

        renderer.Calls.Should().Be(2, "ein Dekodieren je Quelldatei");
        renderer.LastSize.Should().Be(FaceCropDefaults.Size);
        File.Exists(service.GetCropPath(world.LibraryId, a2)).Should().BeTrue();

        service.Request(world.LibraryId, [a1]);
        await WaitUntilAsync(() => Count(ready) == 4);
        renderer.Calls.Should().Be(2, "vorhandener Ausschnitt wird nicht neu gerendert");

        // Index schreibt die Datei neu → Ausschnitte ihrer Gesichter verworfen (Box kann sich geändert haben)
        world.Fx.Messenger.Send(new FacesChangedMessage(world.LibraryId, [files[0]]));
        await WaitUntilAsync(() => !File.Exists(service.GetCropPath(world.LibraryId, a1)) && !File.Exists(service.GetCropPath(world.LibraryId, a2)));
        File.Exists(service.GetCropPath(world.LibraryId, b1)).Should().BeTrue();

        // Gesicht gelöscht → verwaister Ausschnitt verschwindet beim Abgleich
        using (var connection = world.OpenConnection())
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"DELETE FROM Faces WHERE Id = {b1};";
            cmd.ExecuteNonQuery();
        }

        // Der entprellte Abgleich, den die FacesChangedMessage oben angestoßen hat, kann unter Last erst jetzt laufen und den
        // verwaisten Ausschnitt selbst entfernen — dann findet SweepNow nichts mehr. Entscheidend: danach ist er weg.
        service.SweepNow(world.LibraryId).Should().BeInRange(0, 1);
        File.Exists(service.GetCropPath(world.LibraryId, b1)).Should().BeFalse("SweepNow oder der entprellte Abgleich entfernt den verwaisten Ausschnitt");

        service.Request(world.LibraryId, [a1]);
        await WaitUntilAsync(() => File.Exists(service.GetCropPath(world.LibraryId, a1)));
        service.DeleteAll();
        Directory.Exists(Path.Combine(world.Fx.Paths.GetLibraryThumbnailsPath(world.LibraryId), "faces")).Should().BeFalse();
    }

    [Fact]
    public async Task Fehlerhafte_Quelle_wird_in_der_Sitzung_nicht_endlos_wiederholt()
    {
        using var world = await FaceAssignmentWorld.CreateAsync();
        var files = await world.CreateFilesAsync(1);
        var face = world.InsertFace(files[0], world.Embedding("a"));
        var renderer = new FakeCropRenderer { Fail = true };
        using var service = new FaceCropService(world.Fx.ContextFactory, world.Fx.Paths, renderer, NullLogger<FaceCropService>.Instance, world.Fx.Messenger);
        service.Start();

        service.Request(world.LibraryId, [face]);
        await WaitUntilAsync(() => renderer.Calls == 1);
        await Task.Delay(100);
        service.Request(world.LibraryId, [face]);
        await Task.Delay(200);
        renderer.Calls.Should().Be(1);
    }

    private static int Count(List<FaceCropReadyEventArgs> list)
    {
        lock (list)
        {
            return list.Count;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var start = DateTime.UtcNow;
        while (!condition())
        {
            if (DateTime.UtcNow - start > Wait)
            {
                throw new TimeoutException("Bedingung nicht erfüllt");
            }

            await Task.Delay(20);
        }
    }

    private sealed class FakeCropRenderer : IFaceCropRenderer
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public int LastSize { get; private set; }

        public bool Fail { get; init; }

        public void Render(string sourcePath, IReadOnlyList<FaceCropTarget> targets, int size)
        {
            Interlocked.Increment(ref _calls);
            LastSize = size;
            if (Fail)
            {
                throw new IOException("nicht lesbar");
            }

            foreach (var target in targets)
            {
                File.WriteAllBytes(target.TargetPath, [0xFF, 0xD8]);
            }
        }
    }
}
