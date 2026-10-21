using System.Globalization;

namespace PhotoViewerV3.Services.Duplicates;

/// <summary>
/// Vorschlag „behalten“ (ADR-0053) — eine feste, sichtbare Reihenfolge von Kriterien; das erste, in dem sich die beste
/// Datei vom Rest unterscheidet, ist die Begründung auf der Seite:
/// <list type="number">
/// <item>höhere Auflösung (Pixelzahl),</item>
/// <item>Aufnahmedatum aus der Datei bzw. von Hand gesetzt (nicht nur aus dem Dateinamen geraten),</item>
/// <item>liegt schon in der Bibliothek statt im Eingang,</item>
/// <item>mehr eigene Angaben (Bewertung, Favorit, Tags, Kategorien, Personen),</item>
/// <item>größere Datei (bei gleicher Auflösung weniger stark komprimiert),</item>
/// <item>länger im Katalog (kleinere FileId).</item>
/// </list>
/// Die Auswahl bleibt auf der Seite änderbar; mit „Angaben übertragen“ gehen Bewertung, Favorit, Tags, Kategorien und
/// Personen der gelöschten Dateien ohnehin auf die behaltene über.
/// </summary>
public static class KeeperRule
{
    /// <summary>Die Regel in Worten (Kopfzeile der Seite).</summary>
    public const string Description =
        "höhere Auflösung → Aufnahmedatum aus der Datei → liegt schon in der Bibliothek → Bewertung, Favorit, Tags, Kategorien, Personen → größere Datei → länger im Katalog";

    /// <summary>Mitglieder in Vorschlags-Reihenfolge (beste zuerst).</summary>
    public static IReadOnlyList<DuplicateMember> Order(IEnumerable<DuplicateMember> members)
        => members.OrderBy(m => m, Comparer).ToList();

    /// <summary>Begründung, warum <paramref name="best"/> vor <paramref name="second"/> steht.</summary>
    public static string Reason(DuplicateMember best, DuplicateMember? second)
    {
        if (second is null)
        {
            return "einzige Datei";
        }

        if (best.Pixels != second.Pixels)
        {
            return $"höhere Auflösung ({Resolution(best)} statt {Resolution(second)})";
        }

        if (best.HasCaptureDate != second.HasCaptureDate)
        {
            return "Aufnahmedatum aus der Datei vorhanden";
        }

        if (best.IsInInbox != second.IsInInbox)
        {
            return "liegt schon in der Bibliothek";
        }

        if (best.UserDataScore != second.UserDataScore)
        {
            return "mehr eigene Angaben (Bewertung, Favorit, Tags …)";
        }

        if (best.Size != second.Size)
        {
            return "größere Datei (weniger stark komprimiert)";
        }

        return "gleichwertig — die länger im Katalog";
    }

    /// <summary>„4000 × 3000“ bzw. „unbekannt“.</summary>
    public static string Resolution(DuplicateMember member)
        => member.Width is > 0 && member.Height is > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{member.Width} × {member.Height}")
            : "unbekannt";

    private static readonly IComparer<DuplicateMember> Comparer = Comparer<DuplicateMember>.Create(Compare);

    private static int Compare(DuplicateMember a, DuplicateMember b)
    {
        var c = b.Pixels.CompareTo(a.Pixels);
        if (c != 0)
        {
            return c;
        }

        c = b.HasCaptureDate.CompareTo(a.HasCaptureDate);
        if (c != 0)
        {
            return c;
        }

        c = a.IsInInbox.CompareTo(b.IsInInbox); // Bibliothek (false) zuerst
        if (c != 0)
        {
            return c;
        }

        c = b.UserDataScore.CompareTo(a.UserDataScore);
        if (c != 0)
        {
            return c;
        }

        c = b.Size.CompareTo(a.Size);
        return c != 0 ? c : a.FileId.CompareTo(b.FileId);
    }
}
