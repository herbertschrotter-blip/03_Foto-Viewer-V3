using System.Runtime.CompilerServices;
using Lumora.Services.Archive;
using Lumora.Tests.Fixtures;
using static Lumora.Tests.Archive.ArchiveTestHelper;

namespace Lumora.Tests.Archive;

/// <summary>
/// RAR und 7z über eingecheckte Mini-Archive (TestData/, erzeugt mit WinRAR 7 „Rar.exe“ bzw. Windows-bsdtar
/// „tar --format 7zip“; Inhalt: a.jpg, sub/b.png, notes.txt). Erzeugen lassen sich diese Formate im Test nicht.
/// Die Dateien werden über den Quellpfad gefunden (CallerFilePath), damit die csproj unverändert bleibt.
/// </summary>
public sealed class ArchiveFormatTests
{
    private static readonly string[] ExpectedPaths = { "a.jpg", Path.Combine("sub", "b.png"), "notes.txt" };

    [Theory]
    [InlineData("set.rar")]
    [InlineData("set-solid.rar")]
    [InlineData("set.7z")]
    public async Task Rar_und_7z_werden_gelistet_und_entpackt(string fileName)
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var archive = CopyTestData(fileName, fx.OutsideDir);
        var svc = CreateService();

        var listing = await svc.ListAsync(archive);
        listing.Entries.Select(e => e.Path).Should().BeEquivalentTo(ExpectedPaths);
        listing.MediaCount.Should().Be(2);
        listing.HasEncryptedEntries.Should().BeFalse();

        var result = await svc.ExtractAsync(archive, new ArchiveExtractOptions { MediaOnly = false });
        result.Errors.Should().BeEmpty();
        result.FilesExtracted.Should().Be(3);
        result.TargetFolder.Should().Be(Path.Combine(fx.OutsideDir, Path.GetFileNameWithoutExtension(fileName)));
        File.ReadAllText(Path.Combine(result.TargetFolder, "notes.txt")).Should().Be("Rauchtest Hinweis");
        File.ReadAllBytes(Path.Combine(result.TargetFolder, "a.jpg")).Take(4).Should().Equal(0xFF, 0xD8, 0xFF, 0xE0);
        File.Exists(Path.Combine(result.TargetFolder, "sub", "b.png")).Should().BeTrue();
    }

    [Fact]
    public async Task Passwortgeschuetztes_Rar_wird_gelistet_aber_mit_klarer_Meldung_abgelehnt()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var archive = CopyTestData("set-passwort.rar", fx.OutsideDir);
        var svc = CreateService();

        var listing = await svc.ListAsync(archive);
        listing.HasEncryptedEntries.Should().BeTrue();
        listing.FileCount.Should().Be(3);

        var act = () => svc.ExtractAsync(archive, ArchiveExtractOptions.Default);
        (await act.Should().ThrowAsync<ArchiveEncryptedException>()).Which.Message.Should().Contain("passwortgeschützt");
        Directory.Exists(Path.Combine(fx.OutsideDir, "set-passwort")).Should().BeFalse();
    }

    [Fact]
    public async Task Rar_mit_verschluesselten_Koepfen_meldet_Passwortschutz_schon_beim_Listen()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var archive = CopyTestData("set-kopf-verschluesselt.rar", fx.OutsideDir);
        var svc = CreateService();

        var list = () => svc.ListAsync(archive);
        (await list.Should().ThrowAsync<ArchiveEncryptedException>()).Which.Message.Should().Contain("passwortgeschützt");
        var extract = () => svc.ExtractAsync(archive, ArchiveExtractOptions.Default);
        await extract.Should().ThrowAsync<ArchiveEncryptedException>();
    }

    private static string CopyTestData(string fileName, string targetDir, [CallerFilePath] string sourceFile = "")
    {
        var source = Path.Combine(Path.GetDirectoryName(sourceFile)!, "TestData", fileName);
        File.Exists(source).Should().BeTrue($"Testdatei {source} fehlt");
        Directory.CreateDirectory(targetDir);
        var target = Path.Combine(targetDir, fileName);
        File.Copy(source, target, overwrite: true);
        return target;
    }
}
