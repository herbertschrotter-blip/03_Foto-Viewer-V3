using System.Globalization;
using System.Text;

namespace Lumora.Services.Assistant;

/// <summary>
/// Textvergleich des Assistenten (ADR-0063): ohne Groß-/Kleinschreibung und Akzente (ä = a, ß = ss), Leerraum
/// zusammengefasst; <see cref="Stem"/> nimmt einfache deutsche Endungen weg („Sonnenuntergänge“ = „Sonnenuntergang“,
/// „Hunde“ = „Hund“), damit Begriffe aus dem Satz die Namen in Lumora treffen.
/// </summary>
public static class AssistantText
{
    private static readonly string[] Endings = ["innen", "ern", "en", "er", "es", "e", "n", "s"];

    /// <summary>Verwandtschafts-Wörter mit gleicher Bedeutung (für Beziehungen ohne exakt gleichen Namen).</summary>
    private static readonly string[][] Synonyms =
    [
        ["papa", "vater", "papi", "vati"],
        ["mama", "mutter", "mami", "mutti"],
        ["oma", "omi", "grossmutter"],
        ["opa", "opi", "grossvater"],
        ["frau", "ehefrau", "gattin"],
        ["mann", "ehemann", "gatte"],
        ["sohn", "bub"],
        ["tochter"],
        ["schwester", "schwesterchen"],
        ["bruder", "bruderchen"],
        ["freundin", "partnerin"],
        ["freund", "partner"],
        ["hund", "hunde"],
    ];

    /// <summary>Klein, ohne Akzente, ß → ss, Leerraum zusammengefasst, getrimmt.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var decomposed = text.Trim().ToLowerInvariant().Replace("ß", "ss", StringComparison.Ordinal).Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        var space = false;
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                space = true;
                continue;
            }

            if (space && sb.Length > 0)
            {
                sb.Append(' ');
            }

            space = false;
            sb.Append(c);
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>Normalisiert ohne einfache Endungen (nur bei Wörtern ab 4 Buchstaben).</summary>
    public static string Stem(string? text)
    {
        var n = Normalize(text);
        foreach (var ending in Endings)
        {
            if (n.Length - ending.Length >= 3 && n.EndsWith(ending, StringComparison.Ordinal))
            {
                return n[..^ending.Length];
            }
        }

        return n;
    }

    /// <summary>Gleich nach <see cref="Normalize"/>.</summary>
    public static bool Same(string? a, string? b) => Normalize(a) is { Length: > 0 } x && x == Normalize(b);

    /// <summary>Gleich nach <see cref="Normalize"/> oder <see cref="Stem"/>.</summary>
    public static bool Matches(string? name, string? term)
        => Same(name, term) || (Stem(name) is { Length: > 0 } s && s == Stem(term));

    /// <summary>Beziehungs-Name passt zum Wort: gleich, Synonym, oder beginnt mit dem Wort („Oma (mütterlicherseits)“, „Oma Maria“).</summary>
    public static bool RelationMatches(string? relationName, string? word)
    {
        var rel = Normalize(relationName);
        var w = Normalize(word);
        if (rel.Length == 0 || w.Length == 0)
        {
            return false;
        }

        foreach (var variant in Variants(w))
        {
            if (rel == variant || rel.StartsWith(variant + " ", StringComparison.Ordinal) || rel.StartsWith(variant + "(", StringComparison.Ordinal)
                || Stem(rel) == Stem(variant))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Das Wort und seine Synonyme (normalisiert, „meine“/„mein“ entfernt).</summary>
    public static IReadOnlyList<string> Variants(string word)
    {
        var w = Normalize(word);
        foreach (var prefix in new[] { "meiner ", "meinem ", "meinen ", "meine ", "mein ", "unserer ", "unsere ", "unser " })
        {
            if (w.StartsWith(prefix, StringComparison.Ordinal))
            {
                w = w[prefix.Length..];
                break;
            }
        }

        foreach (var group in Synonyms)
        {
            if (group.Contains(w) || group.Contains(Stem(w)))
            {
                return group;
            }
        }

        return [w];
    }
}
