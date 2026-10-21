using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lumora.Services.Sorter;

/// <summary>
/// Gemeinsame JSON-Optionen: PascalCase-Namen wie in den V2-Dateien, Lesen unabhängig von der
/// Schreibweise, lesbare Regex (kein Escaping von „+“, „&lt;“ usw.), eingerückt für Export-Dateien.
/// </summary>
internal static class SorterJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };
}
