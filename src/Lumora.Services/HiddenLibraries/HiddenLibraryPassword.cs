using System.Security.Cryptography;
using System.Text;

namespace Lumora.Services.HiddenLibraries;

/// <summary>
/// Prüfwert eines Lumora-Kennworts (ADR-0049): PBKDF2 mit HMAC-SHA-256, zufälligem Salz und gespeicherter
/// Iterationszahl. Das Kennwort selbst wird nie gespeichert.
/// </summary>
public sealed record PasswordVerifier(string Algorithm, int Iterations, string Salt, string Hash);

/// <summary>Erzeugt und prüft <see cref="PasswordVerifier"/> mit .NET-Bordmitteln (<see cref="Rfc2898DeriveBytes.Pbkdf2(string, byte[], int, HashAlgorithmName, int)"/>).</summary>
public static class HiddenLibraryPassword
{
    public const string Algorithm = "PBKDF2-SHA256";

    /// <summary>Iterationen für neue Kennwörter (OWASP-Empfehlung 2023 für PBKDF2-HMAC-SHA256: mindestens 600 000).</summary>
    public const int DefaultIterations = 600_000;

    /// <summary>Mindestlänge eines Lumora-Kennworts (Zeichen).</summary>
    public const int MinLength = 6;

    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    /// <summary>Prüft die Regeln für ein neues Kennwort; null = in Ordnung, sonst die Meldung für den Dialog.</summary>
    public static string? Validate(string? password, string? repeat)
    {
        if (string.IsNullOrEmpty(password))
        {
            return "Bitte ein Kennwort eingeben.";
        }

        if (password.Length < MinLength)
        {
            return $"Das Kennwort braucht mindestens {MinLength} Zeichen.";
        }

        if (!string.Equals(password, repeat, StringComparison.Ordinal))
        {
            return "Die beiden Kennwörter stimmen nicht überein.";
        }

        return null;
    }

    /// <param name="iterations">Nur Tests verringern sie (Laufzeit); der Prüfwert merkt sich die Zahl.</param>
    public static PasswordVerifier Create(string password, int iterations = DefaultIterations)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        ArgumentOutOfRangeException.ThrowIfLessThan(iterations, 1);
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Derive(password, salt, iterations);
        return new PasswordVerifier(Algorithm, iterations, Convert.ToBase64String(salt), Convert.ToBase64String(hash));
    }

    /// <summary>Vergleicht in konstanter Zeit; unbekanntes Verfahren oder beschädigter Prüfwert = false.</summary>
    public static bool Verify(string? password, PasswordVerifier verifier)
    {
        if (string.IsNullOrEmpty(password)
            || !string.Equals(verifier.Algorithm, Algorithm, StringComparison.Ordinal)
            || verifier.Iterations < 1)
        {
            return false;
        }

        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(verifier.Salt);
            expected = Convert.FromBase64String(verifier.Hash);
        }
        catch (FormatException)
        {
            return false;
        }

        if (expected.Length != HashBytes || salt.Length == 0)
        {
            return false;
        }

        var actual = Derive(password, salt, verifier.Iterations);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static byte[] Derive(string password, byte[] salt, int iterations)
    {
        var bytes = Encoding.UTF8.GetBytes(password);
        try
        {
            return Rfc2898DeriveBytes.Pbkdf2(bytes, salt, iterations, HashAlgorithmName.SHA256, HashBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}
