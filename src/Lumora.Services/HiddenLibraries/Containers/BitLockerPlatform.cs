using System.Globalization;

namespace Lumora.Services.HiddenLibraries.Containers;

/// <summary>BitLocker-Zustand eines Laufwerks (Shell-Eigenschaft <c>System.Volume.BitLockerProtection</c>).</summary>
public enum BitLockerProtection
{
    Unknown = 0,

    /// <summary>Verschlüsselt und entsperrt (Wert 1).</summary>
    On = 1,

    /// <summary>Nicht verschlüsselt (Wert 2).</summary>
    Off = 2,

    /// <summary>Verschlüsselung läuft (Wert 3) — der Assistent ist abgeschlossen.</summary>
    Encrypting = 3,

    Decrypting = 4,

    Suspended = 5,

    /// <summary>Verschlüsselt und gesperrt (Wert 6).</summary>
    Locked = 6,

    /// <summary>Verschlüsselt, aber ohne Schlüsselschutz (Wert 8).</summary>
    WaitingForActivation = 8,
}

/// <summary>BitLocker-Möglichkeiten dieses Windows (ADR-0061); Tests ersetzen sie.</summary>
public interface IBitLockerPlatform
{
    /// <summary>null = BitLocker verfügbar; sonst der Grund für den Benutzer (z. B. Windows Home).</summary>
    string? UnavailableReason { get; }

    /// <summary>Der Windows-eigene BitLocker-Assistent (erhöht): <c>%windir%\System32\BitLockerWizardElev.exe</c>.</summary>
    string WizardPath { get; }

    /// <summary>Befehlszeile für den Assistenten „BitLocker aktivieren“ — wie der Explorer: <c>X:\ T</c>.</summary>
    string WizardArguments(char driveLetter);

    /// <summary>Liest den Zustand ohne Administratorrechte (nur lesend).</summary>
    Task<BitLockerProtection> GetProtectionAsync(char driveLetter, CancellationToken ct);
}

/// <summary>
/// Produktive BitLocker-Abfragen. Verfügbar = Windows-Ausgabe nicht „Home“ (<c>EditionID</c> beginnt nicht mit
/// <c>Core</c>) und der Assistent ist vorhanden. Den Aufruf <c>BitLockerWizardElev.exe %1 T</c> nutzt auch der Explorer
/// (Registry <c>HKCR\Drive\shell\encrypt-bde-elev\command</c>). Der Zustand kommt aus der Shell-Eigenschaft
/// <c>System.Volume.BitLockerProtection</c> — <c>manage-bde -status</c> und WMI <c>Win32_EncryptableVolume</c> bräuchten
/// Administratorrechte (eine zweite Rückfrage).
/// </summary>
public sealed class BitLockerPlatform : IBitLockerPlatform
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(20);

    private readonly IProcessOutputRunner _runner;
    private readonly Func<string?> _editionId;
    private readonly Func<string, bool> _fileExists;

    public BitLockerPlatform(IProcessOutputRunner runner, Func<string?>? editionId = null, Func<string, bool>? fileExists = null)
    {
        _runner = runner;
        _editionId = editionId ?? ReadEditionId;
        _fileExists = fileExists ?? File.Exists;
        WizardPath = Path.Combine(Environment.SystemDirectory, "BitLockerWizardElev.exe");
    }

    public string WizardPath { get; }

    public string? UnavailableReason
    {
        get
        {
            if (!OperatingSystem.IsWindows())
            {
                return "BitLocker gibt es nur unter Windows.";
            }

            if (!IsEditionSupported(_editionId()) || !_fileExists(WizardPath))
            {
                return "Diese Windows-Ausgabe enthält BitLocker nicht (z. B. Windows Home). Bitte VeraCrypt verwenden.";
            }

            return null;
        }
    }

    public string WizardArguments(char driveLetter) => $"{char.ToUpperInvariant(driveLetter)}:\\ T";

    /// <summary>Home-Ausgaben heißen „Core…“ (Core, CoreN, CoreSingleLanguage, CoreCountrySpecific).</summary>
    public static bool IsEditionSupported(string? editionId)
        => !string.IsNullOrWhiteSpace(editionId) && !editionId.StartsWith("Core", StringComparison.OrdinalIgnoreCase);

    public async Task<BitLockerProtection> GetProtectionAsync(char driveLetter, CancellationToken ct)
    {
        var letter = char.ToUpperInvariant(driveLetter);
        if (!char.IsAsciiLetterUpper(letter))
        {
            return BitLockerProtection.Unknown;
        }

        var result = await _runner.RunAsync(PowerShellPath, ProtectionQueryArguments(letter), QueryTimeout, null, ct).ConfigureAwait(false);
        return result.ExitCode == 0 && !result.TimedOut ? ParseProtection(result.Output) : BitLockerProtection.Unknown;
    }

    /// <summary>Windows PowerShell 5.1 aus System32 (nie über PATH).</summary>
    public static string PowerShellPath => Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    /// <summary>Nur lesend: <c>Shell.Application</c> → Laufwerk → <c>System.Volume.BitLockerProtection</c>.</summary>
    public static IReadOnlyList<string> ProtectionQueryArguments(char letter)
        =>
        [
            "-NoProfile", "-NonInteractive", "-Command",
            $"(New-Object -ComObject Shell.Application).NameSpace('{char.ToUpperInvariant(letter)}:').Self.ExtendedProperty('System.Volume.BitLockerProtection')",
        ];

    public static BitLockerProtection ParseProtection(string? output)
    {
        var text = output?.Trim().Split('\n').LastOrDefault(l => l.Trim().Length > 0)?.Trim();
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return BitLockerProtection.Unknown;
        }

        return Enum.IsDefined(typeof(BitLockerProtection), value) ? (BitLockerProtection)value : BitLockerProtection.Unknown;
    }

    private static string? ReadEditionId()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            return key?.GetValue("EditionID") as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
