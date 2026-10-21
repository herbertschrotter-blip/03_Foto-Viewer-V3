using System.Net;
using System.Security.Cryptography;
using System.Text;
using CommunityToolkit.Mvvm.Messaging;
using Lumora.Services.Motifs.Models;
using Lumora.Tests.Fixtures;

namespace Lumora.Tests.Motifs;

/// <summary>
/// Modell-Download der Motiv-Erkennung (ADR-0052) über einen Fake-HTTP-Handler — der echte Download läuft in Tests nie:
/// SHA-256 je Datei, Fortschritt über alle Dateien, falsche Prüfsumme verwirft die Datei, Abbruch lässt nichts liegen,
/// vorhandene Dateien werden nicht erneut geladen, Nachricht danach; die feste Liste entspricht den geprüften Dateien.
/// </summary>
public sealed class MotifModelProviderTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "lumora-motif-model-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Feste_Liste_enthaelt_die_freigegebenen_Dateien_mit_Pruefsummen()
    {
        var info = MotifModelDownloadInfo.Siglip2Base;
        info.BaseUri.ToString().Should().Be("https://huggingface.co/onnx-community/siglip2-base-patch16-224-ONNX/resolve/main/");
        info.Files.Select(f => f.RelativePath).Should().Contain(["onnx/vision_model.onnx", "onnx/text_model_int8.onnx", "tokenizer.json", "preprocessor_config.json"]);
        info.Files.Should().OnlyContain(f => f.Sha256.Length == 64 && f.Size > 0);
        info.TotalSize.Should().BeInRange(680_000_000, 700_000_000, "≈ 690 MB");
        info.License.Should().Be("Apache-2.0");
        info.Files.Where(f => f.Required).Select(f => f.RelativePath).Should().BeEquivalentTo("onnx/vision_model.onnx", "onnx/text_model_int8.onnx", "tokenizer.json");
    }

    [Fact]
    public async Task Laedt_prueft_und_legt_ab_mit_Fortschritt_und_Nachricht()
    {
        var files = SmallFiles();
        var handler = new FakeHandler(files);
        var messenger = new WeakReferenceMessenger();
        var changed = new List<MotifModelState>();
        messenger.Register<List<MotifModelState>, MotifModelChangedMessage>(changed, (r, m) => r.Add(m.State));
        var provider = Create(files, handler, messenger);
        provider.State.Should().Be(MotifModelState.Missing);

        var progress = new List<MotifModelDownloadProgress>();
        await provider.DownloadAsync(new InlineProgress<MotifModelDownloadProgress>(progress.Add), CancellationToken.None);

        provider.State.Should().Be(MotifModelState.Ready);
        File.ReadAllBytes(provider.PathOf("onnx/vision_model.onnx")).Should().Equal(files["onnx/vision_model.onnx"]);
        Directory.GetFiles(_folder, "*.download", SearchOption.AllDirectories).Should().BeEmpty();
        progress.Should().Contain(p => p.Stage == "Wird heruntergeladen");
        progress.Max(p => p.Done).Should().Be(files.Values.Sum(v => (long)v.Length));
        changed.Should().Equal(MotifModelState.Ready);
        handler.Requests.Should().HaveCount(files.Count);

        // Zweiter Aufruf: alles da → nichts laden
        await provider.DownloadAsync(null, CancellationToken.None);
        handler.Requests.Should().HaveCount(files.Count);
    }

    [Fact]
    public async Task Falsche_Pruefsumme_verwirft_die_Datei()
    {
        var files = SmallFiles();
        var handler = new FakeHandler(files) { Corrupt = "tokenizer.json" };
        var provider = Create(files, handler, new WeakReferenceMessenger());

        var act = () => provider.DownloadAsync(null, CancellationToken.None);
        (await act.Should().ThrowAsync<MotifModelDownloadException>()).Which.Message.Should().Contain("tokenizer.json").And.Contain("Prüfsumme");
        File.Exists(provider.PathOf("tokenizer.json")).Should().BeFalse();
        Directory.GetFiles(_folder, "*.download", SearchOption.AllDirectories).Should().BeEmpty();
        provider.State.Should().Be(MotifModelState.Missing);
    }

    [Fact]
    public async Task Abbruch_laesst_keine_halbe_Datei_liegen()
    {
        var files = SmallFiles();
        using var cts = new CancellationTokenSource();
        var handler = new FakeHandler(files) { OnRequest = path => { if (path.EndsWith("text_model_int8.onnx", StringComparison.Ordinal)) { cts.Cancel(); } } };
        var provider = Create(files, handler, new WeakReferenceMessenger());

        var act = () => provider.DownloadAsync(null, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
        Directory.GetFiles(_folder, "*.download", SearchOption.AllDirectories).Should().BeEmpty();
        File.Exists(provider.PathOf("onnx/text_model_int8.onnx")).Should().BeFalse();
        provider.State.Should().Be(MotifModelState.Missing);
    }

    [Fact]
    public async Task Http_Fehler_wird_zur_verstaendlichen_Meldung()
    {
        var files = SmallFiles();
        var provider = Create(files, new FakeHandler(files) { Status = HttpStatusCode.NotFound }, new WeakReferenceMessenger());
        var act = () => provider.DownloadAsync(null, CancellationToken.None);
        (await act.Should().ThrowAsync<MotifModelDownloadException>()).Which.Message.Should().StartWith("Das Modell konnte nicht geladen werden");
    }

    private MotifModelProvider Create(Dictionary<string, byte[]> files, FakeHandler handler, IMessenger messenger)
    {
        var info = new MotifModelDownloadInfo(
            new Uri("https://example.invalid/model/"),
            files.Select(kv => new MotifModelFile(kv.Key, kv.Value.Length, Convert.ToHexStringLower(SHA256.HashData(kv.Value)), kv.Key != "config.json")).ToList(),
            "Apache-2.0");
        return new MotifModelProvider(new TestAppDataPaths(_folder), messenger: messenger, downloadInfo: info, modelFolder: _folder, http: new HttpClient(handler));
    }

    private static Dictionary<string, byte[]> SmallFiles() => new()
    {
        ["onnx/vision_model.onnx"] = Enumerable.Range(0, 3_000_000).Select(i => (byte)(i * 7)).ToArray(),
        ["onnx/text_model_int8.onnx"] = Enumerable.Range(0, 2_000_000).Select(i => (byte)(i * 3)).ToArray(),
        ["tokenizer.json"] = Encoding.UTF8.GetBytes("{\"model\":{}}"),
        ["config.json"] = Encoding.UTF8.GetBytes("{}"),
    };

    private sealed class FakeHandler(Dictionary<string, byte[]> files) : HttpMessageHandler
    {
        public List<string> Requests { get; } = new();

        public string? Corrupt { get; init; }

        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;

        public Action<string>? OnRequest { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath["/model/".Length..];
            Requests.Add(path);
            OnRequest?.Invoke(path);
            if (Status != HttpStatusCode.OK)
            {
                return Task.FromResult(new HttpResponseMessage(Status));
            }

            var bytes = files[path].ToArray();
            if (path == Corrupt)
            {
                bytes[0] ^= 0xFF;
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
