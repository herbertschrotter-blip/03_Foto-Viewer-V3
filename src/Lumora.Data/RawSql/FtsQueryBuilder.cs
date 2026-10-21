using System.Text;

namespace Lumora.Data.RawSql;

/// <summary>
/// Baut aus freier Benutzereingabe einen sicheren FTS5-<c>MATCH</c>-Ausdruck für <c>FilesFts</c>.
/// Rohe Eingaben wie <c>foo-bar</c>, <c>"</c>, <c>*</c>, <c>(</c> oder <c>AND</c> sind in der FTS5-Syntax
/// Operatoren und führen zu „fts5: syntax error“. Deshalb wird jedes Wort als Phrase in
/// Anführungszeichen gesetzt (innere <c>"</c> werden verdoppelt) und optional mit <c>*</c> als
/// Präfix-Suche versehen: <c>IMG_20</c> → <c>"IMG_20"*</c>. Mehrere Wörter werden implizit mit AND
/// verknüpft. Innerhalb einer Phrase zerlegt der unicode61-Tokenizer den Text wie beim Indexieren
/// (Bindestrich, Unterstrich, Punkt sind Trenner), sodass <c>foo-bar</c> die Datei <c>foo-bar.jpg</c>
/// als Phrase „foo bar“ findet.
/// </summary>
public static class FtsQueryBuilder
{
    /// <summary>
    /// Liefert den MATCH-Ausdruck oder <c>null</c>, wenn die Eingabe keinen suchbaren Inhalt hat
    /// (leer, nur Whitespace oder nur Satz-/Sonderzeichen wie <c>*</c> oder <c>-</c>).
    /// </summary>
    /// <param name="text">Freie Benutzereingabe (Suchfeld).</param>
    /// <param name="prefix">True: jedes Wort als Präfix suchen (<c>"wort"*</c>), typisch für Suche-beim-Tippen.</param>
    public static string? FromUserInput(string? text, bool prefix = true)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var phrases = new List<string>();
        foreach (var word in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // Ein Wort ohne Buchstaben/Ziffern (z. B. "-", "*", "(") ergäbe eine leere Phrase → weglassen.
            if (!ContainsLetterOrDigit(word))
            {
                continue;
            }

            var escaped = word.Replace("\"", "\"\"", StringComparison.Ordinal);
            phrases.Add(prefix ? $"\"{escaped}\"*" : $"\"{escaped}\"");
        }

        return phrases.Count == 0 ? null : string.Join(' ', phrases);
    }

    private static bool ContainsLetterOrDigit(string word)
    {
        foreach (var rune in word.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune))
            {
                return true;
            }
        }

        return false;
    }
}
