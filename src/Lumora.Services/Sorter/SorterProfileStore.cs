using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Lumora.Domain.Entities;
using Lumora.Services.Database;

namespace Lumora.Services.Sorter;

/// <summary>
/// Settings-basierte Implementierung von <see cref="ISorterProfileStore"/>. Fehlt ein Eintrag oder
/// ist er nicht lesbar, gelten die V2-Defaults (Prefix, Datum_YYYY-MM-DD, Datum_YYYYMMDD bzw. „Variante“).
/// </summary>
public sealed class SorterProfileStore : ISorterProfileStore
{
    public const string PatternsKey = "sorter.patterns";
    public const string SubLevelsKey = "sorter.sublevels";

    private static readonly UTF8Encoding s_utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly ILibraryContextFactory _factory;
    private readonly ILogger<SorterProfileStore> _logger;

    public SorterProfileStore(ILibraryContextFactory factory, ILogger<SorterProfileStore> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public IReadOnlyList<SorterPattern> DefaultPatterns => CreateDefaultPatterns();

    public IReadOnlyList<SorterSubLevel> DefaultSubLevels => CreateDefaultSubLevels();

    /// <summary>V2-Defaults aus <c>Get-SorterPatterns</c>.</summary>
    public static List<SorterPattern> CreateDefaultPatterns() =>
    [
        new()
        {
            Name = "Prefix",
            Regex = @"^([a-zA-Z]{1,2}\d{2,3})",
            GroupCapture = 1,
            Priority = 10,
            Enabled = true,
            BuiltIn = true,
            Description = "1-2 Buchstaben + 2-3 Ziffern (z.B. p006, pa09, pa170)",
        },
        new()
        {
            Name = "Datum_YYYY-MM-DD",
            Regex = @"(\d{4}-\d{2}-\d{2})",
            GroupCapture = 1,
            Priority = 20,
            Enabled = false,
            BuiltIn = true,
            Description = "Datum im Format YYYY-MM-DD (z.B. 2024-01-15)",
        },
        new()
        {
            Name = "Datum_YYYYMMDD",
            Regex = @"(\d{4}(?:0[1-9]|1[0-2])(?:0[1-9]|[12]\d|3[01]))",
            GroupCapture = 1,
            Priority = 30,
            Enabled = false,
            BuiltIn = true,
            Description = "Datum im Format YYYYMMDD (z.B. 20240115)",
        },
    ];

    /// <summary>V2-Default aus <c>Get-SorterSubLevels</c>.</summary>
    public static List<SorterSubLevel> CreateDefaultSubLevels() =>
    [
        new()
        {
            Name = "Variante",
            Regex = @"_1_(result(?:_\d+)?)\.",
            GroupCapture = 1,
            Enabled = true,
        },
    ];

    // ------------------------------------------------------------------ Patterns

    public async Task<IReadOnlyList<SorterPattern>> GetPatternsAsync(CancellationToken ct = default)
    {
        var json = await ReadSettingAsync(PatternsKey, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return CreateDefaultPatterns();
        }

        try
        {
            return ParsePatterns(json);
        }
        catch (Exception ex) when (ex is JsonException or SorterProfileException)
        {
            _logger.LogWarning(ex, "Setting {Key} ist nicht lesbar — verwende Defaults", PatternsKey);
            return CreateDefaultPatterns();
        }
    }

    public async Task SavePatternsAsync(IReadOnlyList<SorterPattern> patterns, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        var normalized = patterns.Select(Normalize).ToList();
        var errors = ValidatePatterns(normalized);
        if (errors.Count > 0)
        {
            throw new SorterProfileException(errors);
        }

        await WriteSettingAsync(PatternsKey, JsonSerializer.Serialize(normalized, SorterJson.Options), ct).ConfigureAwait(false);
        _logger.LogInformation("Sortierer-Patterns gespeichert: {Count} Einträge", normalized.Count);
    }

    public async Task<IReadOnlyList<SorterPattern>> ResetPatternsAsync(CancellationToken ct = default)
    {
        var defaults = CreateDefaultPatterns();
        await SavePatternsAsync(defaults, ct).ConfigureAwait(false);
        return defaults;
    }

    public IReadOnlyList<string> ValidatePatterns(IReadOnlyList<SorterPattern> patterns)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        var errors = new List<string>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < patterns.Count; i++)
        {
            var p = patterns[i];
            var label = string.IsNullOrWhiteSpace(p.Name) ? $"Pattern {i + 1}" : $"Pattern „{p.Name}“";

            if (string.IsNullOrWhiteSpace(p.Name))
            {
                errors.Add($"{label}: Name fehlt.");
            }
            else if (!names.Add(p.Name.Trim()))
            {
                errors.Add($"{label}: Name ist doppelt.");
            }

            ValidateRegexAndCapture(label, p.Regex, p.GroupCapture, errors);
        }

        return errors;
    }

    // ------------------------------------------------------------------ Sub-Level

    public async Task<IReadOnlyList<SorterSubLevel>> GetSubLevelsAsync(CancellationToken ct = default)
    {
        var json = await ReadSettingAsync(SubLevelsKey, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return CreateDefaultSubLevels();
        }

        try
        {
            return ParseSubLevels(json);
        }
        catch (Exception ex) when (ex is JsonException or SorterProfileException)
        {
            _logger.LogWarning(ex, "Setting {Key} ist nicht lesbar — verwende Defaults", SubLevelsKey);
            return CreateDefaultSubLevels();
        }
    }

    public async Task SaveSubLevelsAsync(IReadOnlyList<SorterSubLevel> subLevels, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subLevels);
        var normalized = subLevels.Select(Normalize).ToList();
        var errors = ValidateSubLevels(normalized);
        if (errors.Count > 0)
        {
            throw new SorterProfileException(errors);
        }

        await WriteSettingAsync(SubLevelsKey, JsonSerializer.Serialize(normalized, SorterJson.Options), ct).ConfigureAwait(false);
        _logger.LogInformation("Sortierer-Sub-Level gespeichert: {Count} Einträge", normalized.Count);
    }

    public async Task<IReadOnlyList<SorterSubLevel>> ResetSubLevelsAsync(CancellationToken ct = default)
    {
        var defaults = CreateDefaultSubLevels();
        await SaveSubLevelsAsync(defaults, ct).ConfigureAwait(false);
        return defaults;
    }

    public IReadOnlyList<string> ValidateSubLevels(IReadOnlyList<SorterSubLevel> subLevels)
    {
        ArgumentNullException.ThrowIfNull(subLevels);
        var errors = new List<string>();
        for (var i = 0; i < subLevels.Count; i++)
        {
            var level = subLevels[i];
            var label = string.IsNullOrWhiteSpace(level.Name) ? $"Sub-Level {i + 1}" : $"Sub-Level „{level.Name}“";
            ValidateRegexAndCapture(label, level.Regex, level.GroupCapture, errors);
        }

        return errors;
    }

    // ------------------------------------------------------------------ Import / Export (V2-Format)

    public async Task ExportPatternsAsync(string filePath, CancellationToken ct = default)
    {
        var patterns = await GetPatternsAsync(ct).ConfigureAwait(false);
        await WriteFileAsync(filePath, JsonSerializer.Serialize(patterns, SorterJson.Options), ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SorterPattern>> ImportPatternsAsync(string filePath, CancellationToken ct = default)
    {
        var json = await ReadFileAsync(filePath, ct).ConfigureAwait(false);
        List<SorterPattern> patterns;
        try
        {
            patterns = ParsePatterns(json);
        }
        catch (JsonException ex)
        {
            throw new SorterProfileException([$"Die Datei ist kein gültiges Pattern-JSON: {ex.Message}"]);
        }

        if (patterns.Count == 0)
        {
            throw new SorterProfileException(["Die Datei enthält keine Patterns."]);
        }

        await SavePatternsAsync(patterns, ct).ConfigureAwait(false);
        return patterns;
    }

    public async Task ExportSubLevelsAsync(string filePath, CancellationToken ct = default)
    {
        var levels = await GetSubLevelsAsync(ct).ConfigureAwait(false);
        await WriteFileAsync(filePath, JsonSerializer.Serialize(levels, SorterJson.Options), ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SorterSubLevel>> ImportSubLevelsAsync(string filePath, CancellationToken ct = default)
    {
        var json = await ReadFileAsync(filePath, ct).ConfigureAwait(false);
        List<SorterSubLevel> levels;
        try
        {
            levels = ParseSubLevels(json);
        }
        catch (JsonException ex)
        {
            throw new SorterProfileException([$"Die Datei ist kein gültiges Sub-Level-JSON: {ex.Message}"]);
        }

        await SaveSubLevelsAsync(levels, ct).ConfigureAwait(false);
        return levels;
    }

    // ------------------------------------------------------------------ Parsen

    /// <summary>Array oder (wie V2 bei einem Eintrag) einzelnes Objekt.</summary>
    private static List<SorterPattern> ParsePatterns(string json)
        => ParseList<SorterPattern>(json).Select(Normalize).ToList();

    private static List<SorterSubLevel> ParseSubLevels(string json)
        => ParseList<SorterSubLevel>(json).Select(Normalize).ToList();

    private static List<T> ParseList<T>(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });

        var root = doc.RootElement;
        var list = new List<T>();
        switch (root.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var element in root.EnumerateArray())
                {
                    if (element.ValueKind == JsonValueKind.Object)
                    {
                        AddIfNotNull(list, element.Deserialize<T>(SorterJson.Options));
                    }
                }

                break;
            case JsonValueKind.Object:
                AddIfNotNull(list, root.Deserialize<T>(SorterJson.Options));
                break;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                break;
            default:
                throw new JsonException("Erwartet wurde ein JSON-Array oder -Objekt.");
        }

        return list;
    }

    private static void AddIfNotNull<T>(List<T> list, T? item)
    {
        if (item is not null)
        {
            list.Add(item);
        }
    }

    private static SorterPattern Normalize(SorterPattern p) => new()
    {
        Name = (p.Name ?? string.Empty).Trim(),
        Regex = p.Regex ?? string.Empty,
        GroupCapture = p.GroupCapture,
        Priority = p.Priority,
        Enabled = p.Enabled,
        BuiltIn = p.BuiltIn,
        Description = p.Description ?? string.Empty,
    };

    private static SorterSubLevel Normalize(SorterSubLevel l) => new()
    {
        Name = (l.Name ?? string.Empty).Trim(),
        Regex = l.Regex ?? string.Empty,
        GroupCapture = l.GroupCapture,
        Enabled = l.Enabled,
    };

    private static void ValidateRegexAndCapture(string label, string? pattern, int groupCapture, List<string> errors)
    {
        if (!SorterPatternEngine.TryCompile(pattern, out var regex, out var error))
        {
            errors.Add(string.IsNullOrWhiteSpace(pattern)
                ? $"{label}: Regex fehlt."
                : $"{label}: ungültiger Regex ({error}).");
            return;
        }

        if (groupCapture < 0)
        {
            errors.Add($"{label}: Capture-Gruppe muss 0 oder größer sein.");
            return;
        }

        var groups = SorterPatternEngine.GroupCount(regex!);
        if (groupCapture > groups)
        {
            errors.Add(groups == 0
                ? $"{label}: Regex hat keine Capture-Gruppe — Capture-Gruppe 0 (ganzer Treffer) wählen oder Klammern setzen."
                : $"{label}: Regex hat nur {groups} Capture-Gruppe(n), Gruppe {groupCapture} gibt es nicht.");
        }
    }

    // ------------------------------------------------------------------ Settings / Dateien

    private async Task<string?> ReadSettingAsync(string key, CancellationToken ct)
    {
        await using var ctx = _factory.Create();
        var row = await ctx.Settings.FindAsync([key], ct).ConfigureAwait(false);
        return row?.Value;
    }

    private async Task WriteSettingAsync(string key, string value, CancellationToken ct)
    {
        await using var ctx = _factory.Create();
        var row = await ctx.Settings.FindAsync([key], ct).ConfigureAwait(false);
        if (row is null)
        {
            ctx.Settings.Add(new Setting { Key = key, Value = value });
        }
        else
        {
            row.Value = value;
        }

        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static async Task<string> ReadFileAsync(string filePath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Die Datei „{filePath}“ wurde nicht gefunden.", filePath);
        }

        return await File.ReadAllTextAsync(filePath, Encoding.UTF8, ct).ConfigureAwait(false);
    }

    private static async Task WriteFileAsync(string filePath, string json, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllTextAsync(filePath, json, s_utf8NoBom, ct).ConfigureAwait(false);
    }
}
