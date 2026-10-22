using System.Globalization;
using System.Text;

namespace Lumora.Services.HiddenLibraries.Containers;

/// <summary>Was „Neuen BitLocker-Container erstellen“ anlegen soll (ADR-0061).</summary>
/// <param name="FilePath">Neue VHDX-Datei (voller Pfad, noch nicht vorhanden).</param>
/// <param name="SizeGigabytes">Höchstgröße; die Datei ist dynamisch erweiterbar und wächst erst mit dem Inhalt.</param>
/// <param name="DriveLetter">Freier Laufwerksbuchstabe für das neue Laufwerk.</param>
/// <param name="VolumeLabel">Neutrale Bezeichnung des Laufwerks.</param>
public sealed record VhdxPlan(string FilePath, int SizeGigabytes, char DriveLetter, string VolumeLabel);

/// <summary>
/// Prüft einen <see cref="VhdxPlan"/> und erzeugt das diskpart-Skript dafür: VHDX anlegen (dynamisch), anhängen, als GPT
/// initialisieren, eine Partition, NTFS schnell formatieren, Buchstabe zuweisen. Das Skript enthält nie ein Kennwort —
/// verschlüsselt wird danach im BitLocker-Assistenten von Windows.
/// </summary>
public static class VhdxScript
{
    public const int MinSizeGigabytes = 1;

    /// <summary>Größte VHDX laut Windows: 64 TB.</summary>
    public const int MaxSizeGigabytes = 64 * 1024;

    public const string DefaultLabel = "Daten";

    public const string DefaultFolderName = "Daten";

    public const string DefaultFileName = "sicherung";

    public const string Extension = ".vhdx";

    /// <summary>Höchstlänge einer NTFS-Bezeichnung.</summary>
    public const int MaxLabelLength = 32;

    /// <summary>Ordnernamen bekannter Cloud-Dienste; ein Container darf nie darin liegen (README, ADR-0049).</summary>
    public static readonly IReadOnlyList<string> CloudFolderMarkers =
        ["OneDrive", "Dropbox", "Google Drive", "GoogleDrive", "My Drive", "Meine Ablage", "iCloudDrive", "iCloud Drive", "Nextcloud", "ownCloud", "pCloud", "MEGA", "Box", "MagentaCLOUD"];

    /// <summary>null = Bezeichnung geeignet, sonst die Meldung.</summary>
    public static string? ValidateLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return "Bitte eine Bezeichnung für das Laufwerk eingeben (z. B. „Daten“).";
        }

        var text = label.Trim();
        if (text.Length > MaxLabelLength)
        {
            return $"Die Bezeichnung darf höchstens {MaxLabelLength} Zeichen lang sein.";
        }

        return text.All(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '-' or '_' or '.')
            ? null
            : "Die Bezeichnung darf nur Buchstaben A–Z, Ziffern, Leerzeichen und - _ . enthalten.";
    }

    /// <summary>null = Speicherort geeignet, sonst die Meldung.</summary>
    /// <param name="cloudFolders">Zusätzliche Cloud-Ordner (z. B. die OneDrive-Pfade aus den Umgebungsvariablen).</param>
    public static string? ValidatePath(string? filePath, IEnumerable<string> cloudFolders)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !Path.IsPathFullyQualified(filePath))
        {
            return "Bitte einen Speicherort für die Containerdatei wählen (z. B. D:\\Daten\\sicherung.vhdx).";
        }

        var full = Path.GetFullPath(filePath.Trim());
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal) || root.Length < 2 || root[1] != ':')
        {
            return "Die Containerdatei muss auf einem lokalen Laufwerk liegen (kein Netzwerkpfad).";
        }

        if (!string.Equals(Path.GetExtension(full), Extension, StringComparison.OrdinalIgnoreCase))
        {
            return "Die Containerdatei muss auf „.vhdx“ enden.";
        }

        // diskpart liest das Skript nicht zuverlässig mit Umlauten — der Pfad bleibt deshalb bei ASCII
        if (full.Any(c => c > 126 || c < 32 || c == '"'))
        {
            return "Bitte im Pfad der Containerdatei nur Buchstaben ohne Umlaute, Ziffern und einfache Zeichen verwenden "
                   + "(Windows-Werkzeug diskpart).";
        }

        if (Path.GetFileNameWithoutExtension(full).Length == 0)
        {
            return "Bitte einen Dateinamen angeben.";
        }

        if (IsInCloudFolder(full, cloudFolders))
        {
            return "Der Container darf nicht in OneDrive oder einem anderen Cloud-Ordner liegen — sonst landet er (oder Teile "
                   + "davon) im Internet. Bitte einen Ordner außerhalb wählen.";
        }

        if (File.Exists(full) || Directory.Exists(full))
        {
            return "Diese Datei gibt es schon. Bitte einen anderen Namen wählen.";
        }

        return null;
    }

    /// <summary>true, wenn der Pfad unter einem der Cloud-Ordner liegt oder ein Ordner wie ein Cloud-Dienst heißt.</summary>
    public static bool IsInCloudFolder(string filePath, IEnumerable<string> cloudFolders)
    {
        var full = Path.GetFullPath(filePath);
        foreach (var cloud in cloudFolders)
        {
            if (string.IsNullOrWhiteSpace(cloud) || !Path.IsPathFullyQualified(cloud))
            {
                continue;
            }

            var folder = Path.GetFullPath(cloud).TrimEnd('\\') + "\\";
            if (full.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        var folders = Path.GetDirectoryName(full)?.Split('\\', StringSplitOptions.RemoveEmptyEntries) ?? [];
        return folders.Skip(1).Any(segment => CloudFolderMarkers.Any(marker =>
            string.Equals(segment, marker, StringComparison.OrdinalIgnoreCase)
            || segment.StartsWith(marker + " - ", StringComparison.OrdinalIgnoreCase) // „OneDrive - Firma“
            || (marker.Length > 4 && segment.StartsWith(marker, StringComparison.OrdinalIgnoreCase))));
    }

    /// <summary>Pfad für diskpart: in doppelte Anführungszeichen (Leerzeichen); <c>"</c> ist in Windows-Pfaden unzulässig.</summary>
    public static string Quote(string value)
    {
        if (value.Contains('"', StringComparison.Ordinal))
        {
            throw new ArgumentException("Anführungszeichen sind hier unzulässig.", nameof(value));
        }

        return "\"" + value + "\"";
    }

    /// <summary>Das diskpart-Skript (ASCII, eine Anweisung je Zeile). Vorher <see cref="ValidatePath"/>/<see cref="ValidateLabel"/>.</summary>
    public static string Build(VhdxPlan plan)
    {
        var file = Quote(Path.GetFullPath(plan.FilePath.Trim()));
        var label = Quote(plan.VolumeLabel.Trim());
        var letter = char.ToUpperInvariant(plan.DriveLetter);
        if (!char.IsAsciiLetterUpper(letter))
        {
            throw new ArgumentException("Ungültiger Laufwerksbuchstabe.", nameof(plan));
        }

        var megabytes = (long)plan.SizeGigabytes * 1024;
        var script = new StringBuilder();
        script.AppendLine("rem Lumora: virtuelle Festplatte fuer BitLocker anlegen (ADR-0061)");
        script.AppendLine(CultureInfo.InvariantCulture, $"create vdisk file={file} maximum={megabytes} type=expandable");
        script.AppendLine(CultureInfo.InvariantCulture, $"select vdisk file={file}");
        script.AppendLine("attach vdisk");
        script.AppendLine("convert gpt");
        script.AppendLine("create partition primary");
        script.AppendLine(CultureInfo.InvariantCulture, $"format fs=ntfs label={label} quick");
        script.AppendLine(CultureInfo.InvariantCulture, $"assign letter={letter}");
        script.AppendLine("exit");
        return script.ToString();
    }

    /// <summary>Aufräumen nach einem halben Lauf: die eben angelegte VHDX abhängen (danach löscht das Skript die Datei).</summary>
    public static string BuildCleanup(string filePath)
    {
        var file = Quote(Path.GetFullPath(filePath.Trim()));
        var script = new StringBuilder();
        script.AppendLine(CultureInfo.InvariantCulture, $"select vdisk file={file}");
        script.AppendLine("detach vdisk");
        script.AppendLine("exit");
        return script.ToString();
    }

    /// <summary>
    /// Der erhöhte Lauf als PowerShell-Befehl (für <c>-EncodedCommand</c>): schreibt das diskpart-Skript erst mit
    /// Administratorrechten nach <c>%SystemRoot%\Temp</c> (dort können normale Prozesse die Datei weder lesen noch
    /// austauschen — anders als ein Skript im Benutzerordner zwischen Schreiben und UAC-Rückfrage), führt
    /// <c>diskpart /s</c> aus, hängt bei einem Fehler eine schon angelegte VHDX wieder ab und löscht sie, und löscht das
    /// Skript immer. Exit-Code = der von diskpart. Enthält kein Kennwort.
    /// </summary>
    public static string BuildElevatedPowerShell(VhdxPlan plan)
    {
        var vhdx = PowerShellLiteral(Path.GetFullPath(plan.FilePath.Trim()));
        var nl = "\r\n";
        return string.Join(nl,
            "$ErrorActionPreference='Stop'",
            "$diskpart=Join-Path $env:SystemRoot 'System32\\diskpart.exe'",
            "$f=Join-Path (Join-Path $env:SystemRoot 'Temp') ('lumora-'+[guid]::NewGuid().ToString('N')+'.txt')",
            "$code=1",
            "try {",
            "[IO.File]::WriteAllText($f,@'",
            Build(plan) + "'@,[Text.Encoding]::ASCII)",
            "& $diskpart /s $f | Out-Null",
            "$code=$LASTEXITCODE",
            $"if($code -ne 0 -and (Test-Path -LiteralPath {vhdx})){{",
            "[IO.File]::WriteAllText($f,@'",
            BuildCleanup(plan.FilePath) + "'@,[Text.Encoding]::ASCII)",
            "& $diskpart /s $f | Out-Null",
            $"Remove-Item -LiteralPath {vhdx} -Force -ErrorAction SilentlyContinue",
            "}",
            "} finally {",
            "Remove-Item -LiteralPath $f -Force -ErrorAction SilentlyContinue",
            "}",
            "exit $code");
    }

    /// <summary>Befehlszeile für <c>powershell.exe</c>: ohne Profil, ohne Rückfragen, Befehl Base64 (UTF-16).</summary>
    public static string ElevatedArguments(VhdxPlan plan)
        => "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand "
           + Convert.ToBase64String(Encoding.Unicode.GetBytes(BuildElevatedPowerShell(plan)));

    /// <summary>Einfach gequoteter PowerShell-Text (<c>'</c> verdoppelt).</summary>
    public static string PowerShellLiteral(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    /// <summary>
    /// Größenvorschlag aus dem freien Platz: die Hälfte, ab 20 GB auf 10 GB abgerundet, höchstens 1024 GB, mindestens 1 GB
    /// (die Datei wächst erst mit dem Inhalt).
    /// </summary>
    public static int SuggestSizeGigabytes(long freeBytes)
    {
        var half = freeBytes / 2 / (1024L * 1024 * 1024);
        if (half >= 20)
        {
            half = half / 10 * 10;
        }

        return (int)Math.Clamp(half, MinSizeGigabytes, 1024);
    }
}
