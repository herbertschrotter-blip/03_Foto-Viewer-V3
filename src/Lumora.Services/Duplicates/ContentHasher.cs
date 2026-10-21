using System.Security.Cryptography;

namespace Lumora.Services.Duplicates;

/// <summary>
/// Inhalts-Prüfsumme für „exakt gleich“ (ADR-0053): SHA-256 über alle Bytes der Datei. SHA-256 statt xxHash128, weil es
/// ohne zusätzliches Paket im .NET-Framework steckt, mit den SHA-Befehlen der CPU schneller rechnet als die Platte liefert
/// (Spike 15) und als kryptografische Prüfsumme auch für den späteren Abgleich mit dem Handy taugt (keine absichtlich
/// erzeugten Kollisionen). Datei geteilt geöffnet (Cloud-Sync, Virenscanner), sequenziell gelesen; ändert sie sich während
/// des Lesens (Größe/Änderungszeit danach anders), gilt das Ergebnis nicht.
/// </summary>
public static class ContentHasher
{
    private const int BufferSize = 1 << 20;

    /// <summary>SHA-256 der Datei (32 Bytes).</summary>
    public static byte[] Compute(string path, CancellationToken ct = default)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            BufferSize,
            FileOptions.SequentialScan);
        return Compute(stream, ct);
    }

    /// <summary>SHA-256 eines Datenstroms ab der aktuellen Position.</summary>
    public static byte[] Compute(Stream stream, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            int read;
            while ((read = stream.Read(buffer, 0, BufferSize)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                hash.AppendData(buffer, 0, read);
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }

        return hash.GetHashAndReset();
    }

    /// <summary>Kleingeschriebene Hex-Darstellung (Log, Gruppen-Schlüssel).</summary>
    public static string ToHex(ReadOnlySpan<byte> hash) => Convert.ToHexStringLower(hash);
}
