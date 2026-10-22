using System.Globalization;

namespace Lumora.Services.HiddenLibraries.Containers;

/// <summary>Angaben zum VeraCrypt-Paket für den Bestätigungsschritt (ADR-0061).</summary>
public static class VeraCryptPackage
{
    public const string WingetId = "IDRIX.VeraCrypt";

    public const string Publisher = "IDRIX (veracrypt.fr)";

    public const string Source = "winget — der Paketmanager von Windows; das Installationsprogramm kommt vom Hersteller IDRIX";

    public const string ApproximateSize = "ca. 40 MB Download";

    public const string License = "VeraCrypt License (Apache License 2.0, Teile unter der TrueCrypt License 3.0)";

    public const string LicenseUrl = "https://www.veracrypt.fr/en/VeraCrypt%20License.html";

    /// <summary>Offizielle Download-Seite, wenn es kein winget gibt.</summary>
    public const string DownloadUrl = "https://www.veracrypt.fr/en/Downloads.html";

    /// <summary>
    /// <c>winget install --id IDRIX.VeraCrypt -e --accept-package-agreements --accept-source-agreements</c> — die Zustimmung
    /// zu Lizenz und Quelle gibt der Benutzer mit seinem Klick auf „Zustimmen und installieren“.
    /// </summary>
    public static IReadOnlyList<string> InstallArguments { get; } =
        ["install", "--id", WingetId, "-e", "--accept-package-agreements", "--accept-source-agreements"];
}

/// <summary>Installiert VeraCrypt über winget — nur nach ausdrücklicher Bestätigung in der Oberfläche; Tests ersetzen ihn.</summary>
public interface IVeraCryptInstaller
{
    bool IsWingetAvailable { get; }

    Task<ContainerCommandResult> InstallAsync(IProgress<string>? progress, CancellationToken ct);
}

/// <summary>
/// winget-Aufruf mit mitgelesener Ausgabe. Das Installationsprogramm fragt selbst nach Administratorrechten (eine
/// Windows-Rückfrage). Ob VeraCrypt danach wirklich da ist, prüft der Aufrufer über <see cref="IVeraCryptLocator"/>.
/// </summary>
public sealed class WingetVeraCryptInstaller : IVeraCryptInstaller
{
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(20);

    private readonly IProcessOutputRunner _runner;
    private readonly Func<string?> _locateWinget;

    public WingetVeraCryptInstaller(IProcessOutputRunner runner, Func<string?>? locateWinget = null)
    {
        _runner = runner;
        _locateWinget = locateWinget ?? LocateWinget;
    }

    public bool IsWingetAvailable => _locateWinget() is not null;

    public async Task<ContainerCommandResult> InstallAsync(IProgress<string>? progress, CancellationToken ct)
    {
        var winget = _locateWinget();
        if (winget is null)
        {
            return new ContainerCommandResult(false, NoWingetMessage);
        }

        var result = await _runner.RunAsync(winget, VeraCryptPackage.InstallArguments, InstallTimeout, progress, ct).ConfigureAwait(false);
        return Describe(result);
    }

    public static string NoWingetMessage =>
        "winget (der Paketmanager von Windows, „App-Installer“) ist auf diesem PC nicht vorhanden. Bitte VeraCrypt von der "
        + "offiziellen Seite veracrypt.fr herunterladen und installieren, danach „Erneut prüfen“.";

    /// <summary>Verständliche Meldung zu einem winget-Lauf (bekannte Rückgabewerte, sonst Code + letzte Ausgabezeile).</summary>
    public static ContainerCommandResult Describe(ProcessOutputResult result)
    {
        if (result.StartFailed)
        {
            return new ContainerCommandResult(false, "winget konnte nicht gestartet werden. " + NoWingetMessage);
        }

        if (result.TimedOut)
        {
            return new ContainerCommandResult(false, "Die Installation hat zu lange gedauert und wurde abgebrochen. Bitte erneut versuchen.");
        }

        var code = unchecked((uint)result.ExitCode);
        var message = code switch
        {
            0 => null,
            0x8A15002B or 0x8A150061 or 0x8A15010D => null, // schon installiert / kein Update nötig
            0x8A150014 => "Das Paket „IDRIX.VeraCrypt“ wurde nicht gefunden. Bitte später erneut versuchen oder VeraCrypt von veracrypt.fr installieren.",
            0x8A15010C => "Die Installation wurde abgebrochen (z. B. die Windows-Rückfrage abgelehnt). Es wurde nichts installiert.",
            0x8A150107 => "Keine Internetverbindung — bitte Verbindung prüfen und erneut versuchen.",
            0x8A150105 => "Nicht genug freier Speicherplatz für die Installation.",
            0x8A150109 or 0x8A15010A => "Windows muss neu gestartet werden, um die Installation abzuschließen. Danach Lumora wieder mit _hidden starten.",
            0x8A15010F => "Die Installation ist durch eine Richtlinie dieses PCs gesperrt.",
            0x8A150101 or 0x8A150102 => "Gerade läuft eine andere Installation. Bitte warten und erneut versuchen.",
            _ => string.Format(CultureInfo.InvariantCulture, "winget meldet Fehler 0x{0:X8}.", code) + LastLine(result.Output),
        };

        return message is null
            ? new ContainerCommandResult(true, "VeraCrypt ist installiert.")
            : new ContainerCommandResult(false, message);
    }

    private static string LastLine(string output)
    {
        var line = output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(l => l.Length > 3 && l.Any(char.IsLetter));
        if (line is null)
        {
            return string.Empty;
        }

        return " " + (line.Length > 200 ? line[..200] + "…" : line);
    }

    /// <summary>winget ist ein App-Ausführungsalias unter <c>%LocalAppData%\Microsoft\WindowsApps</c>.</summary>
    private static string? LocateWinget()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(local))
        {
            return null;
        }

        var alias = Path.Combine(local, "Microsoft", "WindowsApps", "winget.exe");
        return File.Exists(alias) ? alias : null;
    }
}
