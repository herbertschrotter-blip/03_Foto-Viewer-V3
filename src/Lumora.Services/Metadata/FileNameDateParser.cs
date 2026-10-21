using System.Globalization;
using System.Text.RegularExpressions;
using Lumora.Domain.Enums;

namespace Lumora.Services.Metadata;

/// <summary>
/// Aufnahmedatum aus dem Dateinamen (Rückfall ohne Metadaten, ADR-0046). Erkannt wird ein Datum JJJJ MM TT (Jahr 19xx/20xx,
/// gültiger Monat/Tag) mit gleichem Trennzeichen „-“, „_“, „.“ oder ohne, optional gefolgt von der Uhrzeit HH MM SS
/// (optional Millisekunden), z. B.
/// <c>IMG_20240712_153012</c>, <c>VID_20240712_153012</c>, <c>PXL_20240712_153012345</c>, <c>20240712_153012</c>,
/// <c>Screenshot_20240712-153012</c>, <c>Screenshot 2026-09-30 105535</c>, <c>2024-07-12 15.30.12</c>,
/// <c>PHOTO-2024-07-12-15-30-12</c>, <c>signal-2024-07-12-153012</c> (genau) und <c>IMG-20240712-WA0001</c>
/// (WhatsApp, nur Tag). Die Ziffernfolge darf nicht Teil einer längeren Zahl sein. Uhrzeiten im Dateinamen sind
/// Ortszeit (Zeitzone dieses PCs); nur ein Tag ergibt Genauigkeit „Tag“ (12:00 Uhr). Zukünftige Daten zählen nicht.
/// </summary>
public static partial class FileNameDateParser
{
    [GeneratedRegex(
        @"(?<![0-9])(?<y>(?:19|20)[0-9]{2})(?<s>[-_.]?)(?<m>0[1-9]|1[0-2])\k<s>(?<d>0[1-9]|[12][0-9]|3[01])" +
        @"(?:(?:[ _T.-]|\s+at\s+)?(?<H>[01][0-9]|2[0-3])(?<t>[-_.:h]?)(?<M>[0-5][0-9])\k<t>(?<S>[0-5][0-9])(?:[0-9]{1,3})?)?(?![0-9])",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex DatePattern();

    /// <summary>Datum aus dem Dateinamen (mit oder ohne Endung) oder null.</summary>
    public static CaptureDateValue? Parse(string fileName, TimeZoneInfo? zone = null)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        var name = Path.GetFileNameWithoutExtension(fileName);
        foreach (Match match in DatePattern().Matches(name))
        {
            if (TryBuild(match, zone) is { } value)
            {
                return value;
            }
        }

        return null;
    }

    private static CaptureDateValue? TryBuild(Match match, TimeZoneInfo? zone)
    {
        var year = Int(match, "y");
        var month = Int(match, "m");
        var day = Int(match, "d");
        if (day > DateTime.DaysInMonth(year, month))
        {
            return null;
        }

        var hasTime = match.Groups["H"].Success;
        var wall = hasTime
            ? new DateTime(year, month, day, Int(match, "H"), Int(match, "M"), Int(match, "S"), DateTimeKind.Unspecified)
            : new DateTime(year, month, day, 12, 0, 0, DateTimeKind.Unspecified);
        var value = CaptureDateValue.FromWallClock(
            wall,
            offsetMinutes: null,
            CaptureDateSource.FileName,
            hasTime ? CaptureDatePrecision.Exact : CaptureDatePrecision.Day,
            zone);
        return CaptureDateValue.IsPlausible(value.Utc) ? value : null;
    }

    private static int Int(Match match, string group) => int.Parse(match.Groups[group].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture);
}
