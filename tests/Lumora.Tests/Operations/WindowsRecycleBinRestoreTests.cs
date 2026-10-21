using System.Runtime.Versioning;
using System.Text;
using Lumora.Services.Operations;

namespace Lumora.Tests.Operations;

/// <summary>
/// Zurückholen aus dem Papierkorb (ADR-0053): <c>$I</c>-Köpfe beider Versionen lesen, jüngsten passenden Eintrag finden,
/// und einmal gegen die echte Shell (Datei in den Papierkorb und wieder heraus).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsRecycleBinRestoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "Lumora.Tests", "bin-" + Guid.NewGuid().ToString("N"));

    public WindowsRecycleBinRestoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string WriteInfo(string suffix, string originalPath, DateTime deletedUtc, int version = 2, bool withData = true)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.Unicode, leaveOpen: true))
        {
            writer.Write((long)version);
            writer.Write(1234L);
            writer.Write(deletedUtc.ToFileTimeUtc());
            if (version == 2)
            {
                writer.Write(originalPath.Length + 1);
                writer.Write(Encoding.Unicode.GetBytes(originalPath + "\0"));
            }
            else
            {
                var fixedPath = new byte[520];
                Encoding.Unicode.GetBytes(originalPath).CopyTo(fixedPath, 0);
                fixedPath[^2] = 0x41; // Rest hinter der Null darf beliebig sein
                writer.Write(fixedPath);
            }
        }

        var info = Path.Combine(_dir, "$I" + suffix);
        File.WriteAllBytes(info, stream.ToArray());
        if (withData)
        {
            File.WriteAllBytes(Path.Combine(_dir, "$R" + suffix), [1, 2, 3]);
        }

        return info;
    }

    [Fact]
    public void Kopf_beider_Versionen_wird_gelesen()
    {
        var when = new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);
        var v2 = WriteInfo("AAAAAA.jpg", @"D:\Fotos\bild.jpg", when);
        var v1 = WriteInfo("BBBBBB.jpg", @"C:\Alt\x.jpg", when, version: 1);

        WindowsRecycleBin.TryReadInfo(v2).Should().Be((@"D:\Fotos\bild.jpg", when));
        WindowsRecycleBin.TryReadInfo(v1).Should().Be((@"C:\Alt\x.jpg", when));
        File.WriteAllBytes(Path.Combine(_dir, "$Ikaputt"), [1, 2]);
        WindowsRecycleBin.TryReadInfo(Path.Combine(_dir, "$Ikaputt")).Should().BeNull();
    }

    [Fact]
    public void Juengster_passender_Eintrag_nach_dem_Zeitpunkt_mit_vorhandener_Datei()
    {
        var t0 = new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);
        WriteInfo("OLD001.jpg", @"D:\Fotos\bild.jpg", t0.AddDays(-3));
        WriteInfo("NEW001.jpg", @"D:\Fotos\bild.jpg", t0.AddMinutes(1));
        WriteInfo("NEW002.jpg", @"D:\Fotos\bild.jpg", t0.AddMinutes(5), withData: false);
        WriteInfo("OTHER1.jpg", @"D:\Fotos\anderes.jpg", t0.AddMinutes(2));

        var entry = WindowsRecycleBin.FindEntry(_dir, @"d:\fotos\BILD.jpg", t0);

        entry.Should().NotBeNull();
        Path.GetFileName(entry!.DataPath).Should().Be("$RNEW001.jpg", "jüngster mit vorhandener $R-Datei, Groß-/Kleinschreibung egal");
        WindowsRecycleBin.FindEntry(_dir, @"D:\Fotos\bild.jpg", t0.AddMinutes(10)).Should().BeNull("alle älter als der Batch");
        WindowsRecycleBin.FindEntry(Path.Combine(_dir, "fehlt"), @"D:\Fotos\bild.jpg", t0).Should().BeNull();
    }

    /// <summary>Gegen die echte Shell: eine 4-Byte-Datei in den Papierkorb des Benutzers und sofort wieder heraus.</summary>
    [Fact]
    public void Echter_Papierkorb_hin_und_zurueck()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var path = Path.Combine(_dir, $"lumora-test-{Guid.NewGuid():N}.jpg");
        File.WriteAllBytes(path, [0xFF, 0xD8, 0xFF, 0xE0]);
        var written = File.GetLastWriteTimeUtc(path);
        var before = DateTime.UtcNow;
        var bin = new WindowsRecycleBin();

        bin.MoveToRecycleBin(path);
        File.Exists(path).Should().BeFalse();
        bin.RestoreFromRecycleBin(path, before);

        File.Exists(path).Should().BeTrue();
        File.ReadAllBytes(path).Should().Equal(0xFF, 0xD8, 0xFF, 0xE0);
        File.GetLastWriteTimeUtc(path).Should().Be(written, "Zeitstempel bleiben");
        var again = () => bin.RestoreFromRecycleBin(path + ".x", before);
        again.Should().Throw<FileNotFoundException>();
    }
}
