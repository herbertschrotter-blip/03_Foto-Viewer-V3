using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Lumora.Services.Thumbnails;

/// <summary>
/// Liest die Pixelmaße des Originals aus der Eingabe-Beschreibung, die FFmpeg mit <c>-loglevel info</c> auf stderr
/// schreibt (ADR-0043), z. B. <c>Stream #0:0[0x1](und): Video: h264 (High) …, 1920x1080 [SAR 1:1 DAR 16:9], …</c>
/// und darunter <c>Display Matrix: rotation of -90.00 degrees</c>. Ergebnis wie angezeigt: bei ±90° Drehung sind
/// Breite und Höhe vertauscht. Ausgewertet wird nur der Eingabe-Teil (vor „Stream mapping“/„Output #“); ein
/// „Tile Grid“ (HEIF aus Kacheln) hat Vorrang vor der ersten Video-Spur, die dort nur eine Kachel ist; eingebettete
/// Titelbilder (<c>(attached pic)</c>, z. B. Cover in MP4/MKV) werden übersprungen.
/// </summary>
public static partial class FfmpegStreamInfo
{
    [GeneratedRegex(@"Tile Grid:.*?[ ,](\d{1,5})x(\d{1,5})(?=[ ,\[\r\n]|$)", RegexOptions.CultureInvariant)]
    private static partial Regex TileGridRegex();

    [GeneratedRegex(@"Stream #\d+:\d+.*?: Video: .*?[ ,](\d{1,5})x(\d{1,5})(?=[ ,\[\r\n]|$)", RegexOptions.CultureInvariant)]
    private static partial Regex VideoStreamRegex();

    [GeneratedRegex(@"rotation of (-?\d+(?:\.\d+)?) degrees", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex DisplayMatrixRegex();

    [GeneratedRegex(@"^\s*rotate\s*:\s*(-?\d+)", RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex RotateTagRegex();

    /// <summary>Angezeigte Maße des ersten Bild-/Videostroms; null, wenn stderr keine passende Zeile enthält.</summary>
    public static (int Width, int Height)? TryParseDisplaySize(string? stderr)
    {
        if (string.IsNullOrEmpty(stderr))
        {
            return null;
        }

        // Zeilenweise im Eingabe-Teil: Kachelgitter vor Video-Spur; eingebettete Titelbilder („attached pic“) zählen nicht.
        var lines = InputSection(stderr).Split('\n');
        var found = FindStream(lines, TileGridRegex());
        if (found < 0)
        {
            found = FindStream(lines, VideoStreamRegex());
        }

        if (found < 0)
        {
            return null;
        }

        var match = (TileGridRegex().Match(lines[found]) is { Success: true } grid ? grid : VideoStreamRegex().Match(lines[found]));
        if (!int.TryParse(match.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var width)
            || !int.TryParse(match.Groups[2].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var height)
            || width <= 0 || height <= 0)
        {
            return null;
        }

        // Drehung gehört zum gefundenen Strom: dessen eingerückte Folgezeilen bis zum nächsten Strom.
        var quarterTurn = false;
        for (var i = found + 1; i < lines.Length && !lines[i].TrimStart().StartsWith("Stream ", StringComparison.Ordinal); i++)
        {
            var rotation = DisplayMatrixRegex().Match(lines[i]);
            if (rotation.Success && double.TryParse(rotation.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var degrees))
            {
                quarterTurn = IsQuarterTurn(degrees);
                break;
            }

            var tag = RotateTagRegex().Match(lines[i]);
            if (tag.Success && double.TryParse(tag.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tagDegrees))
            {
                quarterTurn = IsQuarterTurn(tagDegrees);
                break;
            }
        }

        return quarterTurn ? (height, width) : (width, height);
    }

    private static int FindStream(string[] lines, Regex regex)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            if (regex.IsMatch(lines[i]) && !lines[i].Contains("(attached pic)", StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool IsQuarterTurn(double degrees)
    {
        var normalized = Math.Abs(Math.Round(degrees)) % 180;
        return normalized == 90;
    }

    private static string InputSection(string stderr)
    {
        var end = stderr.Length;
        foreach (var marker in new[] { "Stream mapping:", "Output #" })
        {
            var index = stderr.IndexOf(marker, StringComparison.Ordinal);
            if (index >= 0 && index < end)
            {
                end = index;
            }
        }

        return stderr[..end];
    }
}
