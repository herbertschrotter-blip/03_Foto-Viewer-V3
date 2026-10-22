using System.Diagnostics;
using System.Globalization;
using Lumora.Services.HiddenLibraries.Containers;

namespace Lumora.Services.Assistant;

/// <summary>Angaben zum Ollama-Paket für den Bestätigungsschritt (ADR-0063; Muster: VeraCrypt, ADR-0061).</summary>
public static class OllamaPackage
{
    public const string WingetId = "Ollama.Ollama";

    public const string Publisher = "Ollama (ollama.com)";

    public const string Source = "winget — der Paketmanager von Windows; das Installationsprogramm kommt von ollama.com";

    public const string ApproximateSize = "ca. 1 GB Download (mit Bibliotheken für Grafikkarten)";

    public const string License = "MIT License";

    public const string DownloadUrl = "https://ollama.com/download/windows";

    /// <summary>
    /// <c>winget install --id Ollama.Ollama -e --accept-package-agreements --accept-source-agreements</c> — die Zustimmung zu
    /// Lizenz und Quelle gibt der Benutzer mit seinem Klick auf „Zustimmen und installieren“.
    /// </summary>
    public static IReadOnlyList<string> InstallArguments { get; } =
        ["install", "--id", WingetId, "-e", "--accept-package-agreements", "--accept-source-agreements"];
}

/// <summary>Ergebnis einer Installation (Text für den Benutzer).</summary>
public sealed record OllamaInstallResult(bool Success, string Message);

/// <summary>Installiert Ollama über winget — nur nach ausdrücklicher Bestätigung in der Oberfläche; Tests ersetzen ihn.</summary>
public interface IOllamaInstaller
{
    bool IsWingetAvailable { get; }

    Task<OllamaInstallResult> InstallAsync(IProgress<string>? progress, CancellationToken ct);
}

/// <summary>winget-Aufruf mit mitgelesener Ausgabe (die Ausgabe geht nie ins Protokoll).</summary>
public sealed class WingetOllamaInstaller(IProcessOutputRunner runner, Func<string?>? locateWinget = null) : IOllamaInstaller
{
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(30);
    private readonly Func<string?> _locateWinget = locateWinget ?? LocateWinget;

    public bool IsWingetAvailable => _locateWinget() is not null;

    public async Task<OllamaInstallResult> InstallAsync(IProgress<string>? progress, CancellationToken ct)
    {
        if (_locateWinget() is not { } winget)
        {
            return new OllamaInstallResult(false, NoWingetMessage);
        }

        var result = await runner.RunAsync(winget, OllamaPackage.InstallArguments, InstallTimeout, progress, ct).ConfigureAwait(false);
        return Describe(result);
    }

    public static string NoWingetMessage =>
        "winget (der Paketmanager von Windows, „App-Installer“) ist auf diesem PC nicht vorhanden. Bitte Ollama von "
        + "ollama.com/download herunterladen und installieren, danach „Erneut prüfen“.";

    /// <summary>Verständliche Meldung zu einem winget-Lauf (bekannte Rückgabewerte, sonst Code).</summary>
    public static OllamaInstallResult Describe(ProcessOutputResult result)
    {
        if (result.StartFailed)
        {
            return new OllamaInstallResult(false, "winget konnte nicht gestartet werden. " + NoWingetMessage);
        }

        if (result.TimedOut)
        {
            return new OllamaInstallResult(false, "Die Installation hat zu lange gedauert und wurde abgebrochen. Bitte erneut versuchen.");
        }

        var code = unchecked((uint)result.ExitCode);
        var message = code switch
        {
            0 => null,
            0x8A15002B or 0x8A150061 or 0x8A15010D => null, // schon installiert / kein Update nötig
            0x8A150014 => "Das Paket „Ollama.Ollama“ wurde nicht gefunden. Bitte später erneut versuchen oder Ollama von ollama.com installieren.",
            0x8A15010C => "Die Installation wurde abgebrochen. Es wurde nichts installiert.",
            0x8A150107 => "Keine Internetverbindung — bitte Verbindung prüfen und erneut versuchen.",
            0x8A150105 => "Nicht genug freier Speicherplatz für die Installation.",
            0x8A150101 or 0x8A150102 => "Gerade läuft eine andere Installation. Bitte warten und erneut versuchen.",
            _ => string.Format(CultureInfo.InvariantCulture, "winget meldet Fehler 0x{0:X8}.", code),
        };

        return message is null ? new OllamaInstallResult(true, "Ollama ist installiert.") : new OllamaInstallResult(false, message);
    }

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

/// <summary>Findet und startet den lokalen Ollama-Dienst (Tests ersetzen ihn).</summary>
public interface IOllamaLocator
{
    /// <summary>Pfad zu <c>ollama.exe</c> oder null.</summary>
    string? FindExecutable();

    /// <summary>Startet den Dienst (Taskleisten-App bzw. <c>ollama serve</c> ohne Fenster); false, wenn nicht installiert.</summary>
    bool TryStart();
}

/// <summary>Standard-Installationsort von Ollama unter Windows (<c>%LocalAppData%\Programs\Ollama</c>) bzw. PATH.</summary>
public sealed class OllamaLocator : IOllamaLocator
{
    public string? FindExecutable()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(local))
        {
            var exe = Path.Combine(local, "Programs", "Ollama", "ollama.exe");
            if (File.Exists(exe))
            {
                return exe;
            }
        }

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var exe = Path.Combine(dir.Trim(), "ollama.exe");
                if (File.Exists(exe))
                {
                    return exe;
                }
            }
            catch (ArgumentException)
            {
            }
        }

        return null;
    }

    public bool TryStart()
    {
        if (FindExecutable() is not { } exe)
        {
            return false;
        }

        try
        {
            var app = Path.Combine(Path.GetDirectoryName(exe) ?? string.Empty, "ollama app.exe");
            var info = File.Exists(app)
                ? new ProcessStartInfo(app) { UseShellExecute = true }
                : new ProcessStartInfo(exe, "serve") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            using var _ = Process.Start(info);
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }
}
