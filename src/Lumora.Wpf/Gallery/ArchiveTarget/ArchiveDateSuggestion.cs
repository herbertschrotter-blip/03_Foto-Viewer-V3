using System;
using System.Globalization;
using System.IO;
using System.Windows.Media.Imaging;

namespace Lumora.Gallery.ArchiveTarget;

/// <summary>
/// Vorschlag „Jahr/Monat“ für den Archiv-Zielordner: Aufnahmedatum aus den EXIF-Daten (WIC
/// <see cref="BitmapMetadata.DateTaken"/>, nur Bilder), sonst das ältere von Erstell- und Änderungsdatum der Datei
/// (beim Kopieren bleibt das Änderungsdatum original, das Erstelldatum wird neu). Ergebnis als relativer Pfad
/// <c>yyyy\yyyy-MM</c>, z. B. <c>2024\2024-07</c> — sortierfähig und auf einen Blick lesbar.
/// </summary>
public static class ArchiveDateSuggestion
{
    public static string RelativePathFor(DateTime date) =>
        Path.Combine(date.ToString("yyyy", CultureInfo.InvariantCulture), date.ToString("yyyy-MM", CultureInfo.InvariantCulture));

    /// <summary>Relativer Vorschlag für eine Datei oder null, wenn sie nicht gelesen werden kann.</summary>
    public static string? SuggestFor(string filePath)
    {
        var date = ResolveDate(filePath);
        return date is { } d ? RelativePathFor(d) : null;
    }

    /// <summary>Aufnahmedatum (EXIF) oder Dateidatum in lokaler Zeit; null, wenn die Datei nicht erreichbar ist.</summary>
    public static DateTime? ResolveDate(string filePath)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(filePath);
            if (!info.Exists)
            {
                return null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }

        return TryReadDateTaken(filePath) ?? (info.CreationTime < info.LastWriteTime ? info.CreationTime : info.LastWriteTime);
    }

    /// <summary>EXIF-Aufnahmedatum über WIC (DelayCreation: nur Header/Metadaten, kein Decode); null ohne EXIF oder bei Fehlern.</summary>
    public static DateTime? TryReadDateTaken(string filePath)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
            if (decoder.Frames.Count == 0 || decoder.Frames[0].Metadata is not BitmapMetadata metadata)
            {
                return null;
            }

            var taken = metadata.DateTaken;
            if (string.IsNullOrWhiteSpace(taken))
            {
                return null;
            }

            // WIC liefert den Wert als Text der aktuellen Kultur; EXIF-Rohformat "yyyy:MM:dd HH:mm:ss" zur Sicherheit auch.
            if (DateTime.TryParse(taken, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var parsed)
                || DateTime.TryParseExact(taken, "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out parsed))
            {
                return parsed.Year > 1900 ? parsed : null;
            }

            return null;
        }
        catch (Exception)
        {
            // Kein WIC-Codec, kein Bild, defekte Datei: Dateidatum übernimmt.
            return null;
        }
    }
}
