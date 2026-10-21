using System.Runtime.Versioning;
using Lumora.Services.Operations;

namespace Lumora.Tests.Operations;

/// <summary>
/// Der echte Papierkorb (SHFileOperation): Netz-/UNC-Pfade müssen als Fehler gemeldet werden, bevor
/// die Shell die Datei mangels Papierkorb endgültig löschen könnte.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsRecycleBinTests
{
    [Theory]
    [InlineData(@"\\server\share\foto.jpg")]
    [InlineData(@"\\?\UNC\server\share\foto.jpg")]
    [InlineData(@"\\localhost\c$\foto.jpg")]
    public void UNC_Pfade_werden_ohne_Dateizugriff_als_nicht_verfuegbar_gemeldet(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        WindowsRecycleBin.GetUnavailableReason(path).Should().Contain("UNC");

        var act = () => new WindowsRecycleBin().MoveToRecycleBin(path);

        act.Should().Throw<IOException>().WithMessage("*Papierkorb nicht verfügbar*nicht gelöscht*");
    }

    [Fact]
    public void Lokales_Festplattenlaufwerk_hat_einen_Papierkorb()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var local = Path.Combine(Path.GetTempPath(), "foto.jpg");

        WindowsRecycleBin.GetUnavailableReason(local).Should().BeNull();
    }

    [Fact]
    public void Fehlende_lokale_Datei_wirft_FileNotFound()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var missing = Path.Combine(Path.GetTempPath(), "Lumora.Tests", "gibt-es-nicht-" + Guid.NewGuid().ToString("N") + ".jpg");

        var act = () => new WindowsRecycleBin().MoveToRecycleBin(missing);

        act.Should().Throw<FileNotFoundException>();
    }

    /// <summary>
    /// Smoke-Test gegen die echte Shell — legt eine 4-Byte-Datei im Papierkorb des Benutzers ab und läuft
    /// deshalb nur bewusst: Umgebungsvariable <c>LUMORA_RECYCLE_SMOKE=1</c> setzen. (Marshalling des
    /// SHFILEOPSTRUCT wurde im Härtungslauf zusätzlich manuell gegen den Papierkorb-Inhalt verifiziert.)
    /// </summary>
    [Fact]
    public void Lokale_Datei_wird_in_den_Papierkorb_verschoben_oder_bleibt_bei_Fehler_liegen()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("LUMORA_RECYCLE_SMOKE") != "1")
        {
            return;
        }

        var dir = Path.Combine(Path.GetTempPath(), "Lumora.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "papierkorb-smoke ä (1).jpg");
        File.WriteAllBytes(path, new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });

        try
        {
            new WindowsRecycleBin().MoveToRecycleBin(path);
            File.Exists(path).Should().BeFalse("nach erfolgreicher Meldung liegt die Datei im Papierkorb");
        }
        catch (IOException)
        {
            // Shell-Fehler (z. B. eingeschränkte Umgebung): dann muss die Datei unangetastet liegen bleiben.
            File.Exists(path).Should().BeTrue("ein gemeldeter Fehler darf nie mit einem Hard-Delete einhergehen");
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // best-effort
            }
        }
    }
}
