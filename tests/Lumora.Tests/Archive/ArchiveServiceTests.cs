using Lumora.Services.Archive;
using Lumora.Tests.Fixtures;
using static Lumora.Tests.Archive.ArchiveTestHelper;

namespace Lumora.Tests.Archive;

public sealed class ArchiveServiceTests
{
    [Fact]
    public void GetDefaultTargetFolder_ist_Ordner_des_Archivs_plus_Name_ohne_Endung()
    {
        var svc = CreateService();
        svc.GetDefaultTargetFolder(@"C:\Fotos\Urlaub 2024.zip").Should().Be(@"C:\Fotos\Urlaub 2024");
        svc.GetDefaultTargetFolder(@"C:\Fotos\backup.tar.gz").Should().Be(@"C:\Fotos\backup");
        svc.GetDefaultTargetFolder(@"C:\Fotos\set.tgz").Should().Be(@"C:\Fotos\set");
        svc.IsArchive(@"C:\Fotos\set.7z").Should().BeTrue();
        svc.IsArchive(@"C:\Fotos\bild.jpg").Should().BeFalse();
    }

    [Fact]
    public async Task ListAsync_liefert_Dateien_mit_Groesse_Datum_und_Medienmarkierung()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var zip = WriteZip(Path.Combine(fx.OutsideDir, "set.zip"),
            ("a.jpg", Jpeg(3000)),
            ("sub/b.mp4", Jpeg(5000)),
            ("sub/notes.txt", Text("hallo")),
            ("inner.zip", ZipBytes(("c.png", Jpeg(100)))),
            ("leer/", Array.Empty<byte>()));

        var listing = await CreateService().ListAsync(zip);

        listing.ArchivePath.Should().Be(zip);
        listing.Entries.Select(e => e.Path).Should().BeEquivalentTo(new[] { "a.jpg", @"sub\b.mp4", @"sub\notes.txt", "inner.zip" });
        listing.FileCount.Should().Be(4);
        listing.MediaCount.Should().Be(2);
        listing.NestedArchiveCount.Should().Be(1);
        listing.HasEncryptedEntries.Should().BeFalse();

        var a = listing.Entries.Single(e => e.Name == "a.jpg");
        a.Size.Should().Be(3000);
        a.CompressedSize.Should().BeGreaterThan(0);
        a.IsMedia.Should().BeTrue();
        a.IsArchive.Should().BeFalse();
        a.ModifiedAtUtc.Should().NotBeNull();
        a.ModifiedAtUtc!.Value.Year.Should().Be(2024);

        listing.Entries.Single(e => e.Name == "notes.txt").IsMedia.Should().BeFalse();
        listing.Entries.Single(e => e.Name == "inner.zip").IsArchive.Should().BeTrue();
        listing.TotalSize.Should().BeGreaterThan(8000);
    }

    [Fact]
    public async Task ExtractAsync_entpackt_in_Standardordner_mit_Unterordnern_und_meldet_Fortschritt()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var zip = WriteZip(Path.Combine(fx.OutsideDir, "urlaub.zip"),
            ("a.jpg", Jpeg(3000)),
            ("sub/deep/b.mp4", Jpeg(5000)),
            ("notes.txt", Text("hallo")));
        var progress = new List<ArchiveExtractProgress>();

        var result = await CreateService().ExtractAsync(zip, new ArchiveExtractOptions { MediaOnly = false }, new InlineProgress<ArchiveExtractProgress>(progress.Add));

        var target = Path.Combine(fx.OutsideDir, "urlaub");
        result.TargetFolder.Should().Be(target);
        result.FilesExtracted.Should().Be(3);
        result.FilesSkipped.Should().Be(0);
        result.Errors.Should().BeEmpty();
        result.Canceled.Should().BeFalse();
        result.Rounds.Should().Be(1);
        result.BytesExtracted.Should().Be(3000 + 5000 + 5);
        File.ReadAllBytes(Path.Combine(target, "a.jpg")).Should().Equal(Jpeg(3000));
        File.Exists(Path.Combine(target, "sub", "deep", "b.mp4")).Should().BeTrue();
        File.ReadAllText(Path.Combine(target, "notes.txt")).Should().Be("hallo");
        Directory.EnumerateFiles(target, "*.lumorapart", SearchOption.AllDirectories).Should().BeEmpty();
        File.GetLastWriteTimeUtc(Path.Combine(target, "a.jpg")).Year.Should().Be(2024);

        // Fortschritt: Start mit 0, Ende mit allen Dateien und Bytes (Progress<T> meldet asynchron → kurz warten).
        await Task.Delay(200);
        progress.Should().NotBeEmpty();
        progress.First().FilesTotal.Should().Be(3);
        progress.First().BytesTotal.Should().Be(3000 + 5000 + 5);
        progress.Max(p => p.FilesDone).Should().Be(3);
        progress.Max(p => p.BytesDone).Should().Be(3000 + 5000 + 5);
        progress.All(p => p.ArchiveName == "urlaub.zip" && p.Round == 0).Should().BeTrue();
    }

    [Fact]
    public async Task ExtractAsync_nur_Medien_laesst_Textdateien_weg_und_nutzt_freien_Zielordner()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var zip = WriteZip(Path.Combine(fx.OutsideDir, "set.zip"), ("a.jpg", Jpeg(100)), ("readme.txt", Text("x")), ("doc.pdf", Text("y")));
        var target = Path.Combine(fx.OutsideDir, "ziel", "tief");

        var result = await CreateService().ExtractAsync(zip, new ArchiveExtractOptions { TargetFolder = target, MediaOnly = true });

        result.FilesExtracted.Should().Be(1);
        Directory.GetFiles(target).Select(Path.GetFileName).Should().BeEquivalentTo(new[] { "a.jpg" });
    }

    [Fact]
    public async Task ExtractAsync_Ueberspringen_Ueberschreiben_Umbenennen()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var zip = WriteZip(Path.Combine(fx.OutsideDir, "set.zip"), ("a.jpg", Jpeg(100)));
        var target = Path.Combine(fx.OutsideDir, "set");
        Directory.CreateDirectory(target);
        File.WriteAllBytes(Path.Combine(target, "a.jpg"), new byte[] { 1, 2, 3 });
        var svc = CreateService();

        var skip = await svc.ExtractAsync(zip, new ArchiveExtractOptions { OverwriteMode = ArchiveOverwriteMode.Skip });
        skip.FilesExtracted.Should().Be(0);
        skip.FilesSkipped.Should().Be(1);
        File.ReadAllBytes(Path.Combine(target, "a.jpg")).Should().Equal(1, 2, 3);

        var rename = await svc.ExtractAsync(zip, new ArchiveExtractOptions { OverwriteMode = ArchiveOverwriteMode.Rename });
        rename.FilesExtracted.Should().Be(1);
        File.ReadAllBytes(Path.Combine(target, "a.jpg")).Should().Equal(1, 2, 3);
        File.ReadAllBytes(Path.Combine(target, "a (2).jpg")).Should().Equal(Jpeg(100));

        var overwrite = await svc.ExtractAsync(zip, new ArchiveExtractOptions { OverwriteMode = ArchiveOverwriteMode.Overwrite });
        overwrite.FilesExtracted.Should().Be(1);
        File.ReadAllBytes(Path.Combine(target, "a.jpg")).Should().Equal(Jpeg(100));
    }

    [Fact]
    public async Task ExtractAsync_blockt_Zip_Slip_Pfade_und_entpackt_den_Rest()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var zip = WriteZip(Path.Combine(fx.OutsideDir, "boese.zip"),
            ("../../entkommen.jpg", Jpeg(50)),
            ("sub/../../../auch.jpg", Jpeg(50)),
            ("C:/Windows/absolut.jpg", Jpeg(50)),
            ("ok/brav.jpg", Jpeg(50)),
            ("frage?zeichen.jpg", Jpeg(50)));

        var result = await CreateService().ExtractAsync(zip, ArchiveExtractOptions.Default);

        var target = Path.Combine(fx.OutsideDir, "boese");
        result.FilesExtracted.Should().Be(2);
        result.Errors.Should().HaveCount(3);
        result.Errors.Select(e => e.Entry).Should().BeEquivalentTo(new[] { "../../entkommen.jpg", "sub/../../../auch.jpg", "C:/Windows/absolut.jpg" });
        File.Exists(Path.Combine(target, "ok", "brav.jpg")).Should().BeTrue();
        File.Exists(Path.Combine(target, "frage_zeichen.jpg")).Should().BeTrue("ungültige Zeichen werden ersetzt statt abgelehnt");
        File.Exists(Path.Combine(fx.OutsideDir, "entkommen.jpg")).Should().BeFalse();
        File.Exists(Path.Combine(fx.RootDir, "entkommen.jpg")).Should().BeFalse();
        File.Exists(Path.Combine(Path.GetTempPath(), "entkommen.jpg")).Should().BeFalse();
        Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories).Should().HaveCount(2);
    }

    [Theory]
    [InlineData(@"C:\ziel", "a/b.jpg", @"C:\ziel\a\b.jpg")]
    [InlineData(@"C:\ziel", "./a/./b.jpg", @"C:\ziel\a\b.jpg")]
    [InlineData(@"C:\ziel", "/abs.jpg", null)]
    [InlineData(@"C:\ziel", "..\\up.jpg", null)]
    [InlineData(@"C:\ziel", "x:y.jpg", null)]
    [InlineData(@"C:\ziel", "", null)]
    [InlineData(@"C:\ziel", "name .jpg", @"C:\ziel\name .jpg")]
    public void ResolveSafePath_haelt_Ziele_im_Zielordner(string root, string key, string? expected)
        => ArchiveService.ResolveSafePath(root, key).Should().Be(expected);

    [Fact]
    public async Task ExtractAsync_entpackt_verschachtelte_Archive_in_Runden_und_entfernt_sie()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var level3 = ZipBytes(("tief.jpg", Jpeg(30)));
        var level2 = ZipBytes(("mitte.jpg", Jpeg(40)), ("ebene3.zip", level3));
        var zip = WriteZip(Path.Combine(fx.OutsideDir, "haupt.zip"), ("oben.jpg", Jpeg(50)), ("ebene2.zip", level2));

        var result = await CreateService().ExtractAsync(zip, new ArchiveExtractOptions { MaxNestedDepth = 3 });

        var target = Path.Combine(fx.OutsideDir, "haupt");
        result.Errors.Should().BeEmpty();
        result.NestedArchivesExtracted.Should().Be(2);
        result.Rounds.Should().Be(3);
        result.FilesExtracted.Should().Be(5, "oben.jpg, ebene2.zip, mitte.jpg, ebene3.zip, tief.jpg");
        File.Exists(Path.Combine(target, "oben.jpg")).Should().BeTrue();
        File.Exists(Path.Combine(target, "ebene2", "mitte.jpg")).Should().BeTrue();
        File.Exists(Path.Combine(target, "ebene2", "ebene3", "tief.jpg")).Should().BeTrue();
        File.Exists(Path.Combine(target, "ebene2.zip")).Should().BeFalse("verschachtelte Archive werden nach dem Entpacken entfernt");
        File.Exists(Path.Combine(target, "ebene2", "ebene3.zip")).Should().BeFalse();
        File.Exists(zip).Should().BeTrue("das Hauptarchiv bleibt ohne Papierkorb-Option liegen");
    }

    [Fact]
    public async Task ExtractAsync_stoppt_bei_maximaler_Tiefe_und_meldet_das_liegengebliebene_Archiv()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var level2 = ZipBytes(("mitte.jpg", Jpeg(40)), ("ebene3.zip", ZipBytes(("tief.jpg", Jpeg(30)))));
        var zip = WriteZip(Path.Combine(fx.OutsideDir, "haupt.zip"), ("ebene2.zip", level2));

        var result = await CreateService().ExtractAsync(zip, new ArchiveExtractOptions { MaxNestedDepth = 1 });

        var target = Path.Combine(fx.OutsideDir, "haupt");
        result.NestedArchivesExtracted.Should().Be(1);
        File.Exists(Path.Combine(target, "ebene2", "mitte.jpg")).Should().BeTrue();
        File.Exists(Path.Combine(target, "ebene2", "ebene3.zip")).Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Entry == "ebene3.zip" && e.Message.Contains("Verschachtelungstiefe"));

        var flat = await CreateService().ExtractAsync(zip, new ArchiveExtractOptions { ExtractNested = false, TargetFolder = Path.Combine(fx.OutsideDir, "flach") });
        flat.FilesExtracted.Should().Be(0, "ohne Verschachtelung ist ein ZIP keine Mediendatei");
        var all = await CreateService().ExtractAsync(zip, new ArchiveExtractOptions { ExtractNested = false, MediaOnly = false, TargetFolder = Path.Combine(fx.OutsideDir, "alles") });
        all.FilesExtracted.Should().Be(1);
        File.Exists(Path.Combine(fx.OutsideDir, "alles", "ebene2.zip")).Should().BeTrue();
    }

    [Fact]
    public async Task ExtractAsync_bricht_ab_und_laesst_keine_Teildateien_zurueck()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var entries = Enumerable.Range(0, 20).Select(i => ($"f{i:00}.jpg", Jpeg(200_000))).ToArray();
        var zip = WriteZip(Path.Combine(fx.OutsideDir, "gross.zip"), entries);
        using var cts = new CancellationTokenSource();
        var progress = new InlineProgress<ArchiveExtractProgress>(p =>
        {
            if (p.FilesDone >= 2)
            {
                cts.Cancel();
            }
        });

        var result = await CreateService().ExtractAsync(zip, ArchiveExtractOptions.Default, progress, cts.Token);

        result.Canceled.Should().BeTrue();
        result.FilesExtracted.Should().BeLessThan(20);
        var target = Path.Combine(fx.OutsideDir, "gross");
        Directory.EnumerateFiles(target, "*.lumorapart").Should().BeEmpty();
        Directory.EnumerateFiles(target, "*.jpg").Should().HaveCount(result.FilesExtracted);
    }

    [Fact]
    public async Task Passwortgeschuetztes_Archiv_wird_gelistet_aber_nicht_entpackt()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var zip = WriteEncryptedZip(Path.Combine(fx.OutsideDir, "geheim.zip"));
        var svc = CreateService();

        var listing = await svc.ListAsync(zip);
        listing.HasEncryptedEntries.Should().BeTrue();
        listing.Entries.Should().ContainSingle(e => e.Name == "geheim.jpg" && e.IsEncrypted);

        var act = () => svc.ExtractAsync(zip, ArchiveExtractOptions.Default);
        (await act.Should().ThrowAsync<ArchiveEncryptedException>()).Which.Message.Should().Contain("passwortgeschützt");
        Directory.Exists(Path.Combine(fx.OutsideDir, "geheim")).Should().BeFalse("vor dem Fehler wird kein Zielordner angelegt");
    }

    [Fact]
    public async Task Kaputtes_Archiv_liefert_klare_Fehlermeldung()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var broken = Path.Combine(fx.OutsideDir, "kaputt.zip");
        File.WriteAllBytes(broken, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        var svc = CreateService();

        var act = () => svc.ListAsync(broken);
        (await act.Should().ThrowAsync<ArchiveFormatException>()).Which.Message.Should().Contain("kaputt.zip");

        var missing = () => svc.ExtractAsync(Path.Combine(fx.OutsideDir, "fehlt.zip"), ArchiveExtractOptions.Default);
        await missing.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task Gzip_und_Tar_laufen_ueber_den_Stream_Leser()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var gz = Path.Combine(fx.OutsideDir, "bild.jpg.gz");
        await using (var fs = File.Create(gz))
        await using (var gzip = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionLevel.Fastest))
        {
            gzip.Write(Jpeg(777));
        }

        var svc = CreateService();
        var listing = await svc.ListAsync(gz);
        listing.Entries.Should().ContainSingle();
        listing.Entries[0].Name.Should().Be("bild.jpg");

        var result = await svc.ExtractAsync(gz, ArchiveExtractOptions.Default);
        result.FilesExtracted.Should().Be(1);
        result.Errors.Should().BeEmpty();
        File.ReadAllBytes(Path.Combine(fx.OutsideDir, "bild.jpg", "bild.jpg")).Should().Equal(Jpeg(777));
    }

    [Fact]
    public async Task Tar_und_TarGz_werden_gelistet_und_entpackt()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var source = Path.Combine(fx.OutsideDir, "quelle");
        Directory.CreateDirectory(Path.Combine(source, "sub"));
        File.WriteAllBytes(Path.Combine(source, "a.jpg"), Jpeg(1500));
        File.WriteAllBytes(Path.Combine(source, "sub", "b.png"), Jpeg(700));
        File.WriteAllText(Path.Combine(source, "notes.txt"), "x");

        var tar = Path.Combine(fx.OutsideDir, "paket.tar");
        System.Formats.Tar.TarFile.CreateFromDirectory(source, tar, includeBaseDirectory: false);
        var tgz = Path.Combine(fx.OutsideDir, "paket2.tar.gz");
        await using (var fs = File.Create(tgz))
        await using (var gzip = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionLevel.Fastest))
        {
            await System.Formats.Tar.TarFile.CreateFromDirectoryAsync(source, gzip, includeBaseDirectory: false);
        }

        var svc = CreateService();
        foreach (var (archive, folder) in new[] { (tar, "paket"), (tgz, "paket2") })
        {
            var listing = await svc.ListAsync(archive);
            listing.Entries.Select(e => e.Path).Should().BeEquivalentTo(new[] { "a.jpg", Path.Combine("sub", "b.png"), "notes.txt" }, archive);
            listing.MediaCount.Should().Be(2);

            var result = await svc.ExtractAsync(archive, ArchiveExtractOptions.Default);
            result.Errors.Should().BeEmpty();
            result.FilesExtracted.Should().Be(2, "nur Medien");
            result.TargetFolder.Should().Be(Path.Combine(fx.OutsideDir, folder), "„x.tar.gz“ → Ordner „x“");
            File.ReadAllBytes(Path.Combine(result.TargetFolder, "sub", "b.png")).Should().Equal(Jpeg(700));
        }
    }

    [Fact]
    public async Task Tar_mit_langen_PAX_Namen_behaelt_den_vollen_Namen()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var longName = new string('x', 140) + ".jpg";
        var source = Path.Combine(fx.OutsideDir, "quelle-lang");
        Directory.CreateDirectory(Path.Combine(source, "tief"));
        File.WriteAllBytes(Path.Combine(source, "tief", longName), Jpeg(64));
        var tar = Path.Combine(fx.OutsideDir, "lang.tar");
        System.Formats.Tar.TarFile.CreateFromDirectory(source, tar, includeBaseDirectory: false);

        var svc = CreateService();
        var listing = await svc.ListAsync(tar);
        listing.Entries.Should().ContainSingle().Which.Path.Should().Be(Path.Combine("tief", longName));
        listing.MediaCount.Should().Be(1);

        var result = await svc.ExtractAsync(tar, ArchiveExtractOptions.Default);
        result.Errors.Should().BeEmpty();
        result.FilesExtracted.Should().Be(1);
        File.Exists(Path.Combine(result.TargetFolder, "tief", longName)).Should().BeTrue();
    }
}
