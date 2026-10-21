using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Lumora.Services.Motifs.Siglip;

/// <summary>
/// Tokenizer des SigLIP-2-Sprachmodells (Gemma-Tokenizer, ADR-0052) aus <c>tokenizer.json</c> — eigene Umsetzung statt
/// eines weiteren NuGet-Pakets (<c>Microsoft.ML.Tokenizers</c> liest Gemma nur aus dem SentencePiece-Modell, das nicht
/// zum Download gehört). Nachgebildet ist genau die Kette dieser Datei:
/// <list type="number">
/// <item>Normalisierer <c>Replace(" " → "▁")</c>, kein vorangestelltes „▁“;</item>
/// <item>Vor-Tokenisierung <c>Split(" ", MergedWithPrevious)</c> — nach dem Normalisieren gibt es kein Leerzeichen
/// mehr, der ganze Text ist ein „Wort“;</item>
/// <item>BPE über Unicode-Zeichen mit den Merges der Datei (niedrigster Rang zuerst, bei Gleichstand links), unbekannte
/// Zeichen als UTF-8-Bytes <c>&lt;0xXX&gt;</c> (<c>byte_fallback</c>);</item>
/// <item>Nachverarbeitung <c>TemplateProcessing</c>: Sequenz + <c>&lt;eos&gt;</c> (Id 1), kein <c>&lt;bos&gt;</c>.</item>
/// </list>
/// Für das Modell: Kleinschreibung (SigLIP 2 wurde mit kleingeschriebenem Text trainiert), auf 64 Token gekürzt bzw. mit
/// <c>&lt;pad&gt;</c> (Id 0) aufgefüllt — das Sprachmodell liest den Satz-Vektor an der letzten Position.
/// Thread-sicher nach dem Laden (nur lesend).
/// </summary>
public sealed class GemmaTokenizer
{
    public const int PadId = 0;
    public const int EosId = 1;
    public const int UnknownId = 3;

    /// <summary>Feste Länge der Modell-Eingabe (<c>max_length</c> in tokenizer_config.json).</summary>
    public const int MaxLength = 64;

    private readonly Dictionary<string, int> _vocab;
    private readonly Dictionary<long, (int Rank, int Merged)> _merges;
    private readonly int[] _byteTokens = new int[256];

    private GemmaTokenizer(Dictionary<string, int> vocab, Dictionary<long, (int, int)> merges)
    {
        _vocab = vocab;
        _merges = merges;
        for (var b = 0; b < 256; b++)
        {
            _byteTokens[b] = vocab.TryGetValue($"<0x{b:X2}>", out var id) ? id : UnknownId;
        }
    }

    public int VocabularySize => _vocab.Count;

    public int MergeCount => _merges.Count;

    /// <summary>Id eines Tokens aus dem Vokabular (Tests) oder null.</summary>
    public int? TokenId(string token) => _vocab.TryGetValue(token, out var id) ? id : null;

    public static GemmaTokenizer Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return Parse(bytes);
    }

    /// <summary>Liest <c>model.vocab</c> und <c>model.merges</c> (Paare als Feld oder als „a b“) einer tokenizer.json.</summary>
    public static GemmaTokenizer Parse(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip });
        Dictionary<string, int>? vocab = null;
        List<(string A, string B)>? mergePairs = null;
        Expect(ref reader, JsonTokenType.StartObject);
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var name = reader.GetString();
            reader.Read();
            if (name == "model" && reader.TokenType == JsonTokenType.StartObject)
            {
                while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                {
                    var inner = reader.GetString();
                    reader.Read();
                    if (inner == "vocab" && reader.TokenType == JsonTokenType.StartObject)
                    {
                        vocab = ReadVocab(ref reader);
                    }
                    else if (inner == "merges" && reader.TokenType == JsonTokenType.StartArray)
                    {
                        mergePairs = ReadMerges(ref reader);
                    }
                    else if (inner == "type" && reader.TokenType == JsonTokenType.String && reader.GetString() != "BPE")
                    {
                        throw new InvalidDataException($"Tokenizer-Modell {reader.GetString()} wird nicht unterstützt (erwartet BPE).");
                    }
                    else
                    {
                        reader.Skip();
                    }
                }
            }
            else
            {
                reader.Skip();
            }
        }

        if (vocab is null || mergePairs is null)
        {
            throw new InvalidDataException("tokenizer.json enthält kein BPE-Vokabular mit Merges.");
        }

        var merges = new Dictionary<long, (int, int)>(mergePairs.Count);
        for (var rank = 0; rank < mergePairs.Count; rank++)
        {
            var (a, b) = mergePairs[rank];
            if (vocab.TryGetValue(a, out var left) && vocab.TryGetValue(b, out var right) && vocab.TryGetValue(a + b, out var merged))
            {
                merges.TryAdd(Key(left, right), (rank, merged));
            }
        }

        return new GemmaTokenizer(vocab, merges);
    }

    /// <summary>Token-Ids ohne Sonderzeichen (normalisiert, BPE) — genau die Folge vor dem <c>&lt;eos&gt;</c>.</summary>
    public List<int> EncodeRaw(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var normalized = text.Replace(' ', '▁');
        var symbols = new List<int>(normalized.Length);
        Span<byte> utf8 = stackalloc byte[4];
        foreach (var rune in normalized.EnumerateRunes())
        {
            var s = rune.ToString();
            if (_vocab.TryGetValue(s, out var id))
            {
                symbols.Add(id);
                continue;
            }

            var count = rune.EncodeToUtf8(utf8);
            for (var i = 0; i < count; i++)
            {
                symbols.Add(_byteTokens[utf8[i]]);
            }
        }

        while (symbols.Count > 1)
        {
            var bestRank = int.MaxValue;
            var bestIndex = -1;
            var bestMerged = 0;
            for (var i = 0; i < symbols.Count - 1; i++)
            {
                if (_merges.TryGetValue(Key(symbols[i], symbols[i + 1]), out var merge) && merge.Rank < bestRank)
                {
                    bestRank = merge.Rank;
                    bestIndex = i;
                    bestMerged = merge.Merged;
                }
            }

            if (bestIndex < 0)
            {
                break;
            }

            symbols[bestIndex] = bestMerged;
            symbols.RemoveAt(bestIndex + 1);
        }

        return symbols;
    }

    /// <summary>
    /// Modell-Eingabe <c>input_ids</c>: kleingeschrieben, Tokens (höchstens 63) + <c>&lt;eos&gt;</c>, mit
    /// <c>&lt;pad&gt;</c> auf <see cref="MaxLength"/> aufgefüllt.
    /// </summary>
    public long[] EncodeForModel(string text)
    {
        var ids = EncodeRaw(text.ToLower(CultureInfo.InvariantCulture));
        var result = new long[MaxLength];
        var count = Math.Min(ids.Count, MaxLength - 1);
        for (var i = 0; i < count; i++)
        {
            result[i] = ids[i];
        }

        result[count] = EosId;
        return result; // Rest = 0 = <pad>
    }

    private static long Key(int left, int right) => ((long)left << 32) | (uint)right;

    private static Dictionary<string, int> ReadVocab(ref Utf8JsonReader reader)
    {
        var vocab = new Dictionary<string, int>(262_144, StringComparer.Ordinal);
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var token = reader.GetString()!;
            reader.Read();
            vocab[token] = reader.GetInt32();
        }

        return vocab;
    }

    private static List<(string, string)> ReadMerges(ref Utf8JsonReader reader)
    {
        var merges = new List<(string, string)>(600_000);
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                // Älteres Format „a b“ (Trennung am ersten Leerzeichen)
                var text = reader.GetString()!;
                var space = text.IndexOf(' ', 1);
                if (space > 0)
                {
                    merges.Add((text[..space], text[(space + 1)..]));
                }
            }
            else if (reader.TokenType == JsonTokenType.StartArray)
            {
                reader.Read();
                var a = reader.GetString()!;
                reader.Read();
                var b = reader.GetString()!;
                reader.Read(); // EndArray
                merges.Add((a, b));
            }
            else
            {
                reader.Skip();
            }
        }

        return merges;
    }

    private static void Expect(ref Utf8JsonReader reader, JsonTokenType type)
    {
        if (!reader.Read() || reader.TokenType != type)
        {
            throw new InvalidDataException("tokenizer.json ist kein gültiges JSON-Objekt.");
        }
    }

    /// <summary>Text der Token-Folge (Tests, Protokoll), z. B. „a|▁photo|▁of“.</summary>
    public string Describe(IEnumerable<int> ids)
    {
        var reverse = new Dictionary<int, string>();
        var wanted = ids.ToHashSet();
        foreach (var (token, id) in _vocab)
        {
            if (wanted.Contains(id))
            {
                reverse[id] = token;
            }
        }

        var sb = new StringBuilder();
        foreach (var id in ids)
        {
            if (sb.Length > 0)
            {
                sb.Append('|');
            }

            sb.Append(reverse.GetValueOrDefault(id, "?"));
        }

        return sb.ToString();
    }
}
