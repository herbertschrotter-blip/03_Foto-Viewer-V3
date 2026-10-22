using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Services.Assistant;
using Lumora.Services.HiddenLibraries.Containers;
using Lumora.Services.Infrastructure;
using Lumora.Tests.Fixtures;

namespace Lumora.Tests.Assistant;

/// <summary>Attrappe des Ollama-Servers auf HTTP-Ebene (kein Netzwerk): Antworten je Pfad, merkt Anfragen.</summary>
internal sealed class FakeOllamaHandler : HttpMessageHandler
{
    public List<(string Path, string Body)> Requests { get; } = [];

    public Func<HttpRequestMessage, string, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } = (_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

    /// <summary>Wird während einer Anfrage aufgerufen (z. B. GPU-Sperre prüfen).</summary>
    public Action? DuringRequest { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request.RequestUri!.AbsolutePath, body));
        DuringRequest?.Invoke();
        return await Respond(request, body, cancellationToken);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

/// <summary>Programmlauf-Attrappe (winget).</summary>
internal sealed class FakeProcessRunner : IProcessOutputRunner
{
    public List<(string File, IReadOnlyList<string> Args)> Runs { get; } = [];

    public ProcessOutputResult Result { get; set; } = new(0, false, "ok");

    public Task<ProcessOutputResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, IProgress<string>? lines, CancellationToken ct)
    {
        Runs.Add((fileName, arguments));
        return Task.FromResult(Result);
    }
}

/// <summary>
/// Ollama-Client gegen eine Server-Attrappe (ADR-0063): nur 127.0.0.1, Chat mit Schema/Temperatur 0/keep_alive/num_gpu,
/// Zeitlimit, Fehler, Modell fehlt, Server nicht erreichbar, abgeschnittene Antwort, Download mit Fortschritt, Größe aus
/// dem Manifest, GPU-Sperre während der Anfrage, Installation nur über den winget-Aufruf mit der richtigen Paket-Id.
/// </summary>
public sealed class OllamaClientTests
{
    private static (OllamaClient Client, FakeOllamaHandler Server) Create()
    {
        var handler = new FakeOllamaHandler();
        return (new OllamaClient(new HttpClient(handler)), handler);
    }

    [Fact]
    public void Nur_der_lokale_Dienst_ist_erlaubt()
    {
        OllamaClient.DefaultBaseAddress.ToString().Should().Be("http://127.0.0.1:11434/");
        FluentActions.Invoking(() => new OllamaClient(new HttpClient(), new Uri("https://api.example.com/"))).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => new OllamaClient(new HttpClient(), new Uri("http://192.168.1.5:11434/"))).Should().Throw<ArgumentException>();
        new OllamaClient(new HttpClient(), new Uri("http://localhost:11434/")).BaseAddress.IsLoopback.Should().BeTrue();
    }

    [Fact]
    public async Task Chat_schickt_Schema_Temperatur_0_und_kurzes_keep_alive()
    {
        var (client, server) = Create();
        server.Respond = (_, _, _) => Task.FromResult(FakeOllamaHandler.Json("""{"message":{"role":"assistant","content":"{\"action\":\"story\"}"},"done":true,"done_reason":"stop"}"""));

        var result = await client.ChatJsonAsync("qwen2.5:3b", "SYS", "Satz", StoryIntentJson.Schema, useGpu: false, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(5), CancellationToken.None);

        result.Content.Should().Be("{\"action\":\"story\"}");
        result.CutOff.Should().BeFalse();
        var (path, body) = server.Requests.Single();
        path.Should().Be("/api/chat");
        var json = JsonNode.Parse(body)!;
        json["model"]!.GetValue<string>().Should().Be("qwen2.5:3b");
        json["stream"]!.GetValue<bool>().Should().BeFalse();
        json["format"]!["required"]!.AsArray().Should().HaveCount(20);
        json["options"]!["temperature"]!.GetValue<int>().Should().Be(0);
        json["options"]!["num_gpu"]!.GetValue<int>().Should().Be(0, "Grafikkarte aus → nur CPU");
        json["keep_alive"]!.GetValue<string>().Should().Be("60s");
        json["messages"]!.AsArray().Select(m => m!["role"]!.GetValue<string>()).Should().Equal("system", "user");
    }

    [Fact]
    public async Task Abgeschnittene_Antwort_Modell_fehlt_Fehler_Zeitlimit_und_kein_Server()
    {
        var (client, server) = Create();
        Task<OllamaChatResult> Chat(TimeSpan? timeout = null)
            => client.ChatJsonAsync("m", "s", "u", "{}", true, TimeSpan.FromSeconds(1), timeout ?? TimeSpan.FromSeconds(5), CancellationToken.None);

        server.Respond = (_, _, _) => Task.FromResult(FakeOllamaHandler.Json("""{"message":{"content":"{\"action\":"},"done_reason":"length"}"""));
        (await Chat()).CutOff.Should().BeTrue();

        server.Respond = (_, _, _) => Task.FromResult(FakeOllamaHandler.Json("""{"error":"model \"m\" not found, try pulling it first"}""", HttpStatusCode.NotFound));
        (await FluentActions.Awaiting(() => Chat()).Should().ThrowAsync<IntentModelException>()).Which.Failure.Should().Be(IntentModelFailure.ModelMissing);

        server.Respond = (_, _, _) => Task.FromResult(FakeOllamaHandler.Json("""{"error":"out of memory"}""", HttpStatusCode.InternalServerError));
        var error = (await FluentActions.Awaiting(() => Chat()).Should().ThrowAsync<IntentModelException>()).Which;
        error.Failure.Should().Be(IntentModelFailure.ServerError);
        error.Message.Should().Contain("out of memory");

        server.Respond = async (_, _, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return FakeOllamaHandler.Json("{}");
        };
        (await FluentActions.Awaiting(() => Chat(TimeSpan.FromMilliseconds(200))).Should().ThrowAsync<IntentModelException>()).Which.Failure.Should().Be(IntentModelFailure.Timeout);

        server.Respond = (_, _, _) => throw new HttpRequestException("Verbindung abgelehnt");
        (await FluentActions.Awaiting(() => Chat()).Should().ThrowAsync<IntentModelException>()).Which.Failure.Should().Be(IntentModelFailure.ServerUnreachable);
        (await client.GetVersionAsync(CancellationToken.None)).Should().BeNull();
        (await client.ListModelsAsync(CancellationToken.None)).Should().BeEmpty();

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await FluentActions.Awaiting(() => client.ChatJsonAsync("m", "s", "u", "{}", true, TimeSpan.Zero, TimeSpan.FromSeconds(5), cts.Token))
            .Should().ThrowAsync<OperationCanceledException>("Abbrechen durch den Benutzer ist kein Zeitlimit");
    }

    [Fact]
    public async Task Status_Download_mit_Fortschritt_und_Groesse_aus_dem_Manifest()
    {
        var (client, server) = Create();
        server.Respond = (request, _, _) => Task.FromResult(request.RequestUri!.AbsolutePath switch
        {
            "/api/version" => FakeOllamaHandler.Json("""{"version":"0.12.3"}"""),
            "/api/tags" => FakeOllamaHandler.Json("""{"models":[{"name":"qwen2.5:3b","size":1929000000}]}"""),
            "/api/pull" => FakeOllamaHandler.Json(
                "{\"status\":\"pulling manifest\"}\n{\"status\":\"downloading\",\"digest\":\"sha256:a\",\"total\":100,\"completed\":40}\n"
                + "{\"status\":\"downloading\",\"digest\":\"sha256:a\",\"total\":100,\"completed\":100}\n{\"status\":\"success\"}\n"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

        (await client.GetVersionAsync(CancellationToken.None)).Should().Be("0.12.3");
        var models = await client.ListModelsAsync(CancellationToken.None);
        OllamaClient.HasModel(models, "qwen2.5:3b").Should().BeTrue();
        OllamaClient.HasModel(models, "qwen2.5:7b").Should().BeFalse();
        var reports = new List<OllamaPullProgress>();
        await client.PullAsync("qwen2.5:3b", new InlineProgress<OllamaPullProgress>(reports.Add), CancellationToken.None);
        reports.Should().Contain(r => r.Completed == 40 && r.Total == 100);
        reports.Last().Status.Should().Be("success");

        server.Respond = (_, _, _) => Task.FromResult(FakeOllamaHandler.Json("{\"status\":\"pulling manifest\"}\n{\"error\":\"file does not exist\"}\n"));
        (await FluentActions.Awaiting(() => client.PullAsync("gibtsnicht", null, CancellationToken.None)).Should().ThrowAsync<IntentModelException>())
            .Which.Message.Should().Contain("file does not exist");

        OllamaRegistryProbe.ManifestUri("qwen2.5:3b")!.ToString().Should().Be("https://registry.ollama.ai/v2/library/qwen2.5/manifests/3b");
        OllamaRegistryProbe.ManifestUri("nutzer/modell")!.ToString().Should().Be("https://registry.ollama.ai/v2/nutzer/modell/manifests/latest");
        OllamaRegistryProbe.ManifestUri("bös artig").Should().BeNull();
        OllamaRegistryProbe.SumManifest("""{"config":{"size":500},"layers":[{"size":1000},{"size":2000}]}""").Should().Be(3500);

        var registry = new FakeOllamaHandler { Respond = (_, _, _) => Task.FromResult(FakeOllamaHandler.Json("""{"config":{"size":10},"layers":[{"size":1929000000}]}""")) };
        (await new OllamaRegistryProbe(new HttpClient(registry)).GetSizeAsync("qwen2.5:3b", CancellationToken.None)).Should().Be(1929000010);
        registry.Requests.Single().Path.Should().Be("/v2/library/qwen2.5/manifests/3b");
    }

    [Fact]
    public async Task Waehrend_der_Anfrage_haelt_der_Assistent_die_Grafikkarten_Sperre()
    {
        var (client, server) = Create();
        var gate = new GpuGate();
        var busyDuring = new List<bool>();
        server.DuringRequest = () => busyDuring.Add(gate.IsBusy);
        server.Respond = (_, _, _) => Task.FromResult(FakeOllamaHandler.Json("""{"message":{"content":"{}"},"done_reason":"stop"}"""));
        var fx = await Fixtures.TempLibraryFixture.CreateAsync();
        using (fx)
        {
            var settings = new AssistantSettingsStore(fx.ContextFactory);
            var model = new OllamaIntentModel(client, settings, NullLogger<OllamaIntentModel>.Instance, gate);
            var prompt = new IntentPrompt("Satz", new DateOnly(2026, 10, 21), [], []);

            await model.InterpretAsync(prompt, CancellationToken.None);
            busyDuring.Should().Equal([true], "Gesichts-/Motiv-Index pausieren, solange Ollama rechnet");
            gate.IsBusy.Should().BeFalse("danach wieder frei");

            await settings.SaveAsync(AssistantSettings.Default with { UseGpu = false });
            await model.InterpretAsync(prompt, CancellationToken.None);
            busyDuring.Should().Equal([true, false], "nur CPU → keine Sperre");
            JsonNode.Parse(server.Requests.Last().Body)!["options"]!["num_gpu"]!.GetValue<int>().Should().Be(0);
        }
    }

    [Fact]
    public async Task Ollama_wird_nur_ueber_winget_mit_der_Paket_Id_installiert()
    {
        var runner = new FakeProcessRunner();
        var installer = new WingetOllamaInstaller(runner, () => @"C:\winget.exe");

        var ok = await installer.InstallAsync(null, CancellationToken.None);

        ok.Success.Should().BeTrue();
        runner.Runs.Should().ContainSingle().Which.Args.Should().Equal("install", "--id", "Ollama.Ollama", "-e", "--accept-package-agreements", "--accept-source-agreements");
        WingetOllamaInstaller.Describe(new ProcessOutputResult(unchecked((int)0x8A150107), false, string.Empty)).Message.Should().Contain("Internetverbindung");
        WingetOllamaInstaller.Describe(new ProcessOutputResult(unchecked((int)0x8A15002B), false, string.Empty)).Success.Should().BeTrue("schon installiert");

        var none = new WingetOllamaInstaller(runner, () => null);
        none.IsWingetAvailable.Should().BeFalse();
        (await none.InstallAsync(null, CancellationToken.None)).Success.Should().BeFalse();
        runner.Runs.Should().HaveCount(1, "ohne winget wird nichts gestartet");
        AssistantModels.Describe("qwen2.5:7b").License.Should().Be("Apache License 2.0");
        AssistantModels.Describe("eigenes:modell").ApproximateSize.Should().Be("unbekannt");
    }
}
