using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Services.Archive;
using Lumora.Services.Media;
using Lumora.Services.Operations;
using Lumora.Services.Scanning;
using Lumora.Tests.Fixtures;

namespace Lumora.Tests.Archive;

/// <summary>ZIP-Erzeugung mit System.IO.Compression (RAR/7z lassen sich ohne externe Werkzeuge nicht erzeugen).</summary>
internal static class ArchiveTestHelper
{
    /// <summary>Legt ein ZIP an; Einträge als (Pfad im Archiv, Inhalt). Pfade mit „/“ ergeben Unterordner.</summary>
    public static string WriteZip(string zipPath, params (string Key, byte[] Content)[] entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);
        using var stream = new FileStream(zipPath, FileMode.Create);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (key, content) in entries)
        {
            var entry = zip.CreateEntry(key, CompressionLevel.Fastest);
            entry.LastWriteTime = new DateTimeOffset(2024, 6, 1, 12, 0, 0, TimeSpan.Zero);
            using var es = entry.Open();
            es.Write(content);
        }

        return zipPath;
    }

    public static byte[] Jpeg(int size = 1024)
    {
        var bytes = new byte[Math.Max(size, 4)];
        bytes[0] = 0xFF;
        bytes[1] = 0xD8;
        bytes[2] = 0xFF;
        bytes[3] = 0xE0;
        for (var i = 4; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(i % 251);
        }

        return bytes;
    }

    public static byte[] Text(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>Bytes eines ZIPs, das als Datei-Inhalt in ein anderes ZIP gelegt werden kann (verschachteltes Archiv).</summary>
    public static byte[] ZipBytes(params (string Key, byte[] Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (key, content) in entries)
            {
                using var es = zip.CreateEntry(key, CompressionLevel.Fastest).Open();
                es.Write(content);
            }
        }

        return ms.ToArray();
    }

    /// <summary>
    /// Handgebautes ZIP mit einem Eintrag, dessen General-Purpose-Flag „encrypted“ (Bit 0) gesetzt ist (Stored, Dummy-Daten):
    /// System.IO.Compression kann keine verschlüsselten ZIPs schreiben, die Erkennung läuft aber nur über das Flag.
    /// </summary>
    public static string WriteEncryptedZip(string zipPath, string entryName = "geheim.jpg")
    {
        var name = Encoding.ASCII.GetBytes(entryName);
        var data = new byte[12 + 16]; // 12 Bytes PKWARE-Encryption-Header + Nutzdaten
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        // Local file header
        w.Write(0x04034b50u);
        w.Write((ushort)20);
        w.Write((ushort)1); // Flags: Bit 0 = encrypted
        w.Write((ushort)0); // Stored
        w.Write((ushort)0);
        w.Write((ushort)0x5821); // Datum 2024-01-01
        w.Write(0u); // CRC
        w.Write((uint)data.Length);
        w.Write((uint)(data.Length - 12));
        w.Write((ushort)name.Length);
        w.Write((ushort)0);
        w.Write(name);
        w.Write(data);

        var cdOffset = (uint)ms.Position;

        // Central directory header
        w.Write(0x02014b50u);
        w.Write((ushort)20);
        w.Write((ushort)20);
        w.Write((ushort)1);
        w.Write((ushort)0);
        w.Write((ushort)0);
        w.Write((ushort)0x5821);
        w.Write(0u);
        w.Write((uint)data.Length);
        w.Write((uint)(data.Length - 12));
        w.Write((ushort)name.Length);
        w.Write((ushort)0);
        w.Write((ushort)0);
        w.Write((ushort)0);
        w.Write((ushort)0);
        w.Write(0u);
        w.Write(0u); // Local header offset
        w.Write(name);

        var cdSize = (uint)ms.Position - cdOffset;

        // End of central directory
        w.Write(0x06054b50u);
        w.Write((ushort)0);
        w.Write((ushort)0);
        w.Write((ushort)1);
        w.Write((ushort)1);
        w.Write(cdSize);
        w.Write(cdOffset);
        w.Write((ushort)0);
        w.Flush();

        Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);
        File.WriteAllBytes(zipPath, ms.ToArray());
        return zipPath;
    }

    public static ArchiveService CreateService(IScanService? scanner = null, IFileOperationService? fileOps = null)
        => new(new MediaFormatRegistry(), NullLogger<ArchiveService>.Instance, scanner, fileOps);

    /// <summary>Echter ScanService auf der Fixture (für Incremental nach dem Entpacken), mit dem Fixture-Messenger.</summary>
    public static ScanService CreateScanService(TempLibraryFixture fx)
        => new(
            fx.ContextFactory,
            new FolderEnumerator(fx.Formats, NullLogger<FolderEnumerator>.Instance),
            new Lumora.Data.RawSql.BulkScanWriter(),
            new Lumora.Data.RawSql.FtsIndexer(),
            new Lumora.Services.Library.LibraryService(fx.ContextFactory, fx.Paths, NullLogger<Lumora.Services.Library.LibraryService>.Instance),
            NullLogger<ScanService>.Instance,
            fx.Messenger);
}
