using Microsoft.Data.Sqlite;
using Lumora.Services.Database;

namespace Lumora.Services.Assistant;

/// <summary>
/// Einstellungen › Assistent (ADR-0063), Schlüssel <c>assistant.*</c> in der Settings-Tabelle des Katalogs — je Katalog,
/// in einer versteckten Bibliothek also eigene.
/// </summary>
/// <param name="Enabled">Eingabeleiste auf der Seite Storys.</param>
/// <param name="Model">Ollama-Modellname („qwen2.5:3b“).</param>
/// <param name="UseGpu">Grafikkarte nutzen (sonst nur CPU, <c>num_gpu = 0</c>).</param>
/// <param name="RememberHistory">„Zuletzt gefragt“ im Katalog merken.</param>
public sealed record AssistantSettings(bool Enabled = true, string Model = AssistantModels.DefaultModel, bool UseGpu = true, bool RememberHistory = true)
{
    public const string EnabledKey = "assistant.enabled";
    public const string ModelKey = "assistant.model";
    public const string UseGpuKey = "assistant.useGpu";
    public const string HistoryKey = "assistant.history";

    public static AssistantSettings Default { get; } = new();

    /// <summary>Modellname ohne Leerraum; leer bzw. ungültig → Standard.</summary>
    public AssistantSettings Normalize()
    {
        var model = (Model ?? string.Empty).Trim();
        var valid = model.Length is > 0 and <= 120 && model.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or ':' or '/');
        return this with { Model = valid ? model : AssistantModels.DefaultModel };
    }
}

/// <summary>Lesen/Speichern der Assistent-Einstellungen.</summary>
public interface IAssistantSettingsStore
{
    Task<AssistantSettings> LoadAsync(CancellationToken ct = default);

    Task SaveAsync(AssistantSettings settings, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class AssistantSettingsStore(ILibraryContextFactory factory) : IAssistantSettingsStore
{
    public Task<AssistantSettings> LoadAsync(CancellationToken ct = default)
        => Task.Run(() =>
        {
            using var connection = factory.OpenSqliteConnection();
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT Key, Value FROM Settings WHERE Key LIKE 'assistant.%';";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                values[reader.GetString(0)] = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            }

            var d = AssistantSettings.Default;
            return new AssistantSettings(
                Bool(values, AssistantSettings.EnabledKey, d.Enabled),
                values.GetValueOrDefault(AssistantSettings.ModelKey) ?? d.Model,
                Bool(values, AssistantSettings.UseGpuKey, d.UseGpu),
                Bool(values, AssistantSettings.HistoryKey, d.RememberHistory)).Normalize();
        }, ct);

    public async Task SaveAsync(AssistantSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var s = settings.Normalize();
        var values = new Dictionary<string, string>
        {
            [AssistantSettings.EnabledKey] = Flag(s.Enabled),
            [AssistantSettings.ModelKey] = s.Model,
            [AssistantSettings.UseGpuKey] = Flag(s.UseGpu),
            [AssistantSettings.HistoryKey] = Flag(s.RememberHistory),
        };
        await Task.Run(async () =>
        {
            using var connection = factory.OpenSqliteConnection();
            using var tx = await factory.BusyRetry.BeginTransactionAsync(connection, ct).ConfigureAwait(false);
            foreach (var (key, value) in values)
            {
                Upsert(connection, tx, key, value);
            }

            tx.Commit();
        }, ct).ConfigureAwait(false);
    }

    private static void Upsert(SqliteConnection connection, SqliteTransaction tx, string key, string value)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO Settings (Key, Value) VALUES ($k, $v) ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    private static string Flag(bool value) => value ? "true" : "false";

    private static bool Bool(Dictionary<string, string> values, string key, bool fallback)
        => values.TryGetValue(key, out var text) && bool.TryParse(text, out var b) ? b : fallback;
}

/// <summary>Vorgeschlagenes Sprachmodell mit Angaben für die Bestätigung vor dem Download.</summary>
public sealed record AssistantModelInfo(string Name, string Title, string ApproximateSize, long ApproximateBytes, string License, string Note);

/// <summary>
/// Modellwahl (ADR-0063): kleine, deutschfähige Instruct-Modelle, die auf einer GTX 1080 Ti (11 GB, Pascal) zügig
/// laufen und Ollamas JSON-Schema-Ausgabe beherrschen. Der Name ist frei konfigurierbar.
/// </summary>
public static class AssistantModels
{
    public const string DefaultModel = "qwen2.5:3b";

    public const string Source = "Ollama-Bibliothek (registry.ollama.ai), geladen über den lokalen Ollama-Dienst";

    public static IReadOnlyList<AssistantModelInfo> Suggested { get; } =
    [
        new(DefaultModel, "Qwen 2.5 · 3B Instruct (Alibaba Qwen)", "ca. 1,9 GB", 1_929_000_000L,
            "Qwen Research License (frei für private, nicht-kommerzielle Nutzung)",
            "Schnell (≈ 1–3 s je Satz auf einer GTX 1080 Ti), gutes Deutsch, belegt ≈ 2,5 GB Grafikspeicher."),
        new("qwen2.5:7b", "Qwen 2.5 · 7B Instruct (Alibaba Qwen)", "ca. 4,7 GB", 4_683_000_000L,
            "Apache License 2.0",
            "Zuverlässiger bei langen Sätzen, ≈ 3–6 s je Satz, belegt ≈ 5,5 GB Grafikspeicher."),
    ];

    /// <summary>Angaben zum Modell; unbekannte Namen mit „wird vor dem Download ermittelt“.</summary>
    public static AssistantModelInfo Describe(string model)
        => Suggested.FirstOrDefault(m => string.Equals(OllamaClient.WithTag(m.Name), OllamaClient.WithTag(model), StringComparison.OrdinalIgnoreCase))
           ?? new AssistantModelInfo(model, model, "unbekannt", 0, "siehe ollama.com/library (Modellseite)", "Eigenes Modell.");
}
