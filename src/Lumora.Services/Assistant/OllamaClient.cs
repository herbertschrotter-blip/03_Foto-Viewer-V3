using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lumora.Services.Assistant;

/// <summary>Ein installiertes Modell laut <c>/api/tags</c>.</summary>
public sealed record OllamaModelInfo(string Name, long Size);

/// <summary>Fortschritt von <c>/api/pull</c>: Bytes über alle Teile, Status-Text von Ollama.</summary>
public sealed record OllamaPullProgress(string Status, long Completed, long Total)
{
    public double Fraction => Total > 0 ? Math.Clamp((double)Completed / Total, 0, 1) : 0;
}

/// <summary>Antwort von <c>/api/chat</c>.</summary>
public sealed record OllamaChatResult(string Content, bool CutOff);

/// <summary>
/// HTTP-Client für den LOKALEN Ollama-Dienst (ADR-0063) — ausschließlich <c>http://127.0.0.1:11434</c> (andere Adressen
/// lehnt der Konstruktor ab). Kein Schlüssel, keine Cloud. Fehler werden zu <see cref="IntentModelException"/> mit Text
/// für den Benutzer; Satzinhalte landen nie im Protokoll (der Client protokolliert gar nicht).
/// </summary>
public sealed class OllamaClient
{
    public static readonly Uri DefaultBaseAddress = new("http://127.0.0.1:11434/");

    /// <summary>Höchstens so viele Antwort-Tokens — die Absicht ist kurz; mehr deutet auf eine Schleife des Modells.</summary>
    public const int MaxResponseTokens = 600;

    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;

    public OllamaClient(HttpClient http, Uri? baseAddress = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        var address = baseAddress ?? DefaultBaseAddress;
        if (!address.IsLoopback || address.Scheme != Uri.UriSchemeHttp)
        {
            throw new ArgumentException("Ollama wird nur lokal angesprochen (http://127.0.0.1).", nameof(baseAddress));
        }

        _http = http;
        BaseAddress = address;
    }

    public Uri BaseAddress { get; }

    /// <summary>Version des laufenden Dienstes oder null, wenn er nicht antwortet.</summary>
    public async Task<string?> GetVersionAsync(CancellationToken ct)
    {
        try
        {
            using var timeout = Linked(ct, TimeSpan.FromSeconds(3));
            using var response = await _http.GetAsync(new Uri(BaseAddress, "api/version"), timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var node = JsonNode.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
            return node?["version"]?.GetValue<string>() ?? string.Empty;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Installierte Modelle; leer, wenn der Dienst nicht antwortet.</summary>
    public async Task<IReadOnlyList<OllamaModelInfo>> ListModelsAsync(CancellationToken ct)
    {
        try
        {
            using var timeout = Linked(ct, TimeSpan.FromSeconds(5));
            using var response = await _http.GetAsync(new Uri(BaseAddress, "api/tags"), timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            var node = JsonNode.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
            var list = new List<OllamaModelInfo>();
            foreach (var model in node?["models"]?.AsArray() ?? [])
            {
                var name = model?["name"]?.GetValue<string>() ?? model?["model"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    list.Add(new OllamaModelInfo(name, model?["size"]?.GetValue<long>() ?? 0));
                }
            }

            return list;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException && !ct.IsCancellationRequested)
        {
            return [];
        }
    }

    /// <summary>true, wenn das Modell installiert ist („qwen2.5:3b“; ohne Tag zählt „:latest“).</summary>
    public static bool HasModel(IReadOnlyList<OllamaModelInfo> models, string model)
    {
        var wanted = WithTag(model);
        return models.Any(m => string.Equals(WithTag(m.Name), wanted, StringComparison.OrdinalIgnoreCase));
    }

    public static string WithTag(string model) => model.Contains(':', StringComparison.Ordinal) ? model.Trim() : model.Trim() + ":latest";

    /// <summary>
    /// Chat mit erzwungenem JSON-Schema, Temperatur 0 und kurzem <paramref name="keepAlive"/> (danach entlädt Ollama das
    /// Modell). <paramref name="useGpu"/> = false → nur CPU (<c>num_gpu = 0</c>).
    /// </summary>
    public async Task<OllamaChatResult> ChatJsonAsync(string model, string system, string user, string schema, bool useGpu, TimeSpan keepAlive, TimeSpan timeout, CancellationToken ct)
    {
        var options = new JsonObject { ["temperature"] = 0, ["num_predict"] = MaxResponseTokens, ["seed"] = 1 };
        if (!useGpu)
        {
            options["num_gpu"] = 0;
        }

        var body = new JsonObject
        {
            ["model"] = model,
            ["stream"] = false,
            ["format"] = JsonNode.Parse(schema),
            ["options"] = options,
            ["keep_alive"] = ((int)keepAlive.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture) + "s",
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = user },
            },
        };

        using var limit = Linked(ct, timeout);
        try
        {
            using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(new Uri(BaseAddress, "api/chat"), content, limit.Token).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(limit.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw Failure(response.StatusCode, text, model);
            }

            var node = JsonNode.Parse(text);
            var answer = node?["message"]?["content"]?.GetValue<string>() ?? string.Empty;
            var reason = node?["done_reason"]?.GetValue<string>();
            return new OllamaChatResult(answer, string.Equals(reason, "length", StringComparison.Ordinal));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IntentModelException($"Die Sprach-KI hat nicht innerhalb von {timeout.TotalSeconds:0} Sekunden geantwortet.", IntentModelFailure.Timeout);
        }
        catch (HttpRequestException ex)
        {
            throw Unreachable(ex);
        }
        catch (JsonException ex)
        {
            throw new IntentModelException("Ollama hat unerwartet geantwortet.", IntentModelFailure.ServerError, ex);
        }
    }

    /// <summary>Modell sofort entladen (<c>keep_alive = 0</c>); Fehler werden ignoriert.</summary>
    public async Task UnloadAsync(string model, CancellationToken ct)
    {
        try
        {
            using var limit = Linked(ct, TimeSpan.FromSeconds(5));
            var body = new JsonObject { ["model"] = model, ["keep_alive"] = 0 };
            using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var _ = await _http.PostAsync(new Uri(BaseAddress, "api/generate"), content, limit.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            // nicht schlimm: Ollama entlädt nach keep_alive ohnehin
        }
    }

    /// <summary>
    /// Lädt ein Modell über Ollama (<c>/api/pull</c>, gestreamt) — NUR nach Bestätigung in der Oberfläche aufrufen.
    /// Abbruch über <paramref name="ct"/>; Ollama setzt einen späteren Download fort.
    /// </summary>
    public async Task PullAsync(string model, IProgress<OllamaPullProgress>? progress, CancellationToken ct)
    {
        var body = new JsonObject { ["model"] = model, ["stream"] = true };
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(BaseAddress, "api/pull"))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw Failure(response.StatusCode, await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false), model);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var parts = new Dictionary<string, (long Completed, long Total)>(StringComparer.Ordinal);
            var success = false;
            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var node = JsonNode.Parse(line);
                if (node?["error"]?.GetValue<string>() is { } error)
                {
                    throw new IntentModelException("Ollama meldet beim Herunterladen: " + Shorten(error), IntentModelFailure.ServerError);
                }

                var status = node?["status"]?.GetValue<string>() ?? string.Empty;
                if (node?["digest"]?.GetValue<string>() is { } digest && node["total"] is { } total)
                {
                    parts[digest] = (node["completed"]?.GetValue<long>() ?? 0, total.GetValue<long>());
                }

                success |= status == "success";
                progress?.Report(new OllamaPullProgress(status, parts.Values.Sum(p => p.Completed), parts.Values.Sum(p => p.Total)));
            }

            if (!success)
            {
                throw new IntentModelException("Der Download wurde unterbrochen. Bitte erneut versuchen — Ollama setzt ihn fort.", IntentModelFailure.ServerError);
            }
        }
        catch (HttpRequestException ex)
        {
            throw Unreachable(ex);
        }
        catch (JsonException ex)
        {
            throw new IntentModelException("Ollama hat beim Herunterladen unerwartet geantwortet.", IntentModelFailure.ServerError, ex);
        }
    }

    private static IntentModelException Unreachable(Exception ex)
        => new("Ollama läuft nicht (keine Antwort von 127.0.0.1:11434). Bitte Ollama starten oder unter Einstellungen › Assistent installieren.",
            IntentModelFailure.ServerUnreachable, ex);

    private static IntentModelException Failure(HttpStatusCode status, string text, string model)
    {
        string? error = null;
        try
        {
            error = JsonNode.Parse(text)?["error"]?.GetValue<string>();
        }
        catch (JsonException)
        {
        }

        if (status == HttpStatusCode.NotFound || (error?.Contains("not found", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            return new IntentModelException($"Das Sprachmodell „{model}“ ist noch nicht heruntergeladen (Einstellungen › Assistent).", IntentModelFailure.ModelMissing);
        }

        return new IntentModelException($"Ollama meldet einen Fehler ({(int)status}){(error is null ? string.Empty : ": " + Shorten(error))}.", IntentModelFailure.ServerError);
    }

    private static string Shorten(string text) => text.Length <= 160 ? text : text[..160] + "…";

    private static CancellationTokenSource Linked(CancellationToken ct, TimeSpan timeout)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        return cts;
    }
}

/// <summary>
/// Größe eines Modells aus dem Manifest der Ollama-Registry (<c>registry.ollama.ai</c>) — wird erst abgefragt, wenn der
/// Benutzer auf „Herunterladen…“ klickt, damit die Bestätigung die genaue Größe zeigt. Gesendet wird nur der Modellname.
/// </summary>
public interface IModelManifestProbe
{
    /// <summary>Größe in Bytes oder null (offline, unbekanntes Modell).</summary>
    Task<long?> GetSizeAsync(string model, CancellationToken ct);
}

/// <inheritdoc />
public sealed class OllamaRegistryProbe(HttpClient http) : IModelManifestProbe
{
    public static readonly Uri Registry = new("https://registry.ollama.ai/");

    public async Task<long?> GetSizeAsync(string model, CancellationToken ct)
    {
        if (ManifestUri(model) is not { } uri)
        {
            return null;
        }

        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(10));
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.docker.distribution.manifest.v2+json"));
            using var response = await http.SendAsync(request, limit.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return SumManifest(await response.Content.ReadAsStringAsync(limit.Token).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>„qwen2.5:3b“ → …/v2/library/qwen2.5/manifests/3b; „nutzer/modell:tag“ → …/v2/nutzer/modell/manifests/tag.</summary>
    public static Uri? ManifestUri(string model)
    {
        var full = OllamaClient.WithTag(model);
        var colon = full.LastIndexOf(':');
        var name = full[..colon];
        var tag = full[(colon + 1)..];
        if (name.Length == 0 || tag.Length == 0 || name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '/'))
            || tag.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')))
        {
            return null;
        }

        var path = name.Contains('/', StringComparison.Ordinal) ? name : "library/" + name;
        return new Uri(Registry, $"v2/{path}/manifests/{tag}");
    }

    /// <summary>Summe aus <c>config.size</c> und <c>layers[].size</c>; null, wenn nichts davon da ist.</summary>
    public static long? SumManifest(string json)
    {
        var node = JsonNode.Parse(json);
        long total = node?["config"]?["size"]?.GetValue<long>() ?? 0;
        foreach (var layer in node?["layers"]?.AsArray() ?? [])
        {
            total += layer?["size"]?.GetValue<long>() ?? 0;
        }

        return total > 0 ? total : null;
    }
}
